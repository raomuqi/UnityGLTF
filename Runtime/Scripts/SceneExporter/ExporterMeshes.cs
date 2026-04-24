using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GLTF.Schema;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGLTF.Extensions;

#if UNITY_EDITOR // required for in-editor access to non-readable meshes
using UnityEditor;
#endif

namespace UnityGLTF
{
	public partial class GLTFSceneExporter
	{
		// Unity 支持最多 8 个 UV 通道 (TexCoord0 ~ TexCoord7)，glTF 规范对 TEXCOORD_n 没有数量上限。
		private const int MaxUVChannels = 8;

		// UV 通道对应的 VertexAttribute 枚举（顺序严格对应 0~7）
		private static readonly VertexAttribute[] UVAttributes =
		{
			VertexAttribute.TexCoord0,
			VertexAttribute.TexCoord1,
			VertexAttribute.TexCoord2,
			VertexAttribute.TexCoord3,
			VertexAttribute.TexCoord4,
			VertexAttribute.TexCoord5,
			VertexAttribute.TexCoord6,
			VertexAttribute.TexCoord7,
		};

		// UV 通道对应的 glTF semantic 名称（顺序严格对应 0~7）
		private static readonly string[] UVSemantics =
		{
			SemanticProperties.TEXCOORD_0,
			SemanticProperties.TEXCOORD_1,
			SemanticProperties.TEXCOORD_2,
			SemanticProperties.TEXCOORD_3,
			"TEXCOORD_4",
			"TEXCOORD_5",
			"TEXCOORD_6",
			"TEXCOORD_7",
		};

		private struct MeshAccessors
		{
			public AccessorId aPosition, aNormal, aTangent, aColor0, aJoints0, aWeights0;
			// 统一用数组承载所有 UV 通道的 Accessor，彻底解除 UV 数量硬编码限制
			public AccessorId[] aTexcoords;
			public Dictionary<int, MeshPrimitive> subMeshPrimitives;
		}

		private struct BlendShapeAccessors
		{
			public List<Dictionary<string, AccessorId>> targets;
			public List<Double> weights;
			public List<string> targetNames;
			internal SkinnedMeshRenderer firstSkinnedMeshRenderer; 
		}

		private readonly Dictionary<Mesh, MeshAccessors> _meshToPrims = new Dictionary<Mesh, MeshAccessors>();
		private readonly Dictionary<Mesh, BlendShapeAccessors> _meshToBlendShapeAccessors = new Dictionary<Mesh, BlendShapeAccessors>();
		private readonly Dictionary<SkinnedMeshRenderer, List<double>> _NodeBlendShapeWeights = new Dictionary<SkinnedMeshRenderer, List<double>>();

		public void RegisterPrimitivesWithNode(Node node, List<UniquePrimitive> uniquePrimitives)
		{
			// associate unity meshes with gltf mesh id
			foreach (var primKey in uniquePrimitives)
			{
				_primOwner[primKey] = node.Mesh;
			}
		}

		private static List<UniquePrimitive> GetUniquePrimitivesFromGameObjects(IEnumerable<GameObject> primitives)
		{
			var primKeys = new List<UniquePrimitive>();

			foreach (var prim in primitives)
			{
				Mesh meshObj = null;
				SkinnedMeshRenderer smr = null;
				var filter = prim.GetComponent<MeshFilter>();
				if (filter)
				{
					meshObj = filter.sharedMesh;
				}
				else
				{
					smr = prim.GetComponent<SkinnedMeshRenderer>();
					if (smr)
					{
						meshObj = smr.sharedMesh;
					}
				}

				if (!meshObj)
				{
					Debug.LogWarning($"MeshFilter.sharedMesh on GameObject:{prim.name} is missing, skipping", prim);
					exportPrimitiveMarker.End();
					return null;
				}


#if UNITY_EDITOR
				if (!MeshIsReadable(meshObj) && EditorUtility.IsPersistent(meshObj))
				{
#if UNITY_2019_3_OR_NEWER
					var assetPath = AssetDatabase.GetAssetPath(meshObj);
					if (assetPath?.Length > 30) assetPath = "..." + assetPath.Substring(assetPath.Length - 30);
					var otherOption = Application.isPlaying ? "No, skip mesh" : "Cancel export";
					if(EditorUtility.DisplayDialog("Exporting mesh but mesh is not readable",
							$"The mesh {meshObj.name} is not readable. Do you want to change its import settings and make it readable now?\n\n" + assetPath,
							"Make it readable", otherOption,
							DialogOptOutDecisionType.ForThisSession, MakeMeshReadableDialogueDecisionKey))
#endif
					{
						var path = AssetDatabase.GetAssetPath(meshObj);
						var importer = AssetImporter.GetAtPath(path) as ModelImporter;
						if (importer)
						{
							importer.isReadable = true;
							importer.SaveAndReimport();
						}
					}
#if UNITY_2019_3_OR_NEWER
					else
					{
						if (Application.isPlaying)
						{
							Debug.LogWarning(null, $"The mesh {meshObj.name} is not readable. Skipping", meshObj);
							exportPrimitiveMarker.End();
						}
						else
						{
							Debug.LogError(null, $"The mesh {meshObj.name} is not readable and you decided to cancel the export. Canceling", meshObj);
							exportPrimitiveMarker.End();
							throw new OperationCanceledException($"Canceled export because a mesh ({meshObj}) is not readable.");
						}
						return null;
					}
#endif
				}
#endif

				if (Application.isPlaying && !MeshIsReadable(meshObj))
				{
					Debug.LogWarning($"The mesh {meshObj.name} is not readable. Skipping", null);
					exportPrimitiveMarker.End();
					return null;
				}

				var renderer = prim.GetComponent<MeshRenderer>();
				if (!renderer) smr = prim.GetComponent<SkinnedMeshRenderer>();

				if(!renderer && !smr)
				{
					Debug.LogWarning("GameObject does have neither renderer nor SkinnedMeshRenderer! " + prim.name, prim);
					exportPrimitiveMarker.End();
					return null;
				}

				var materialsObj = renderer ? renderer.sharedMaterials : smr.sharedMaterials;

				var primKey = new UniquePrimitive();
				primKey.Mesh = meshObj;
				primKey.Materials = materialsObj;
				primKey.SkinnedMeshRenderer = smr;

				primKeys.Add(primKey);
			}

			return primKeys;
		}

		public NodeId ExportNode(GameObject gameObject) => ExportNode(gameObject.transform);

		/// <summary>
		/// Convenience wrapper around ExportMesh(string, List<UniquePrimitive>)
		/// </summary>
		public MeshId ExportMesh(Mesh mesh)
		{
			var uniquePrimitives = new List<UniquePrimitive>
			{
				new UniquePrimitive()
				{
					Mesh = mesh,
					SkinnedMeshRenderer = null,
					Materials = new [] { DefaultMaterial },
				}
			};
			return ExportMesh(mesh.name, uniquePrimitives);
		}

		public MeshId ExportMesh(string name, List<UniquePrimitive> uniquePrimitives)
		{
			exportMeshMarker.Begin();

			// check if this set of primitives is already a mesh
			MeshId existingMeshId = null;

			foreach (var prim in uniquePrimitives)
			{
				MeshId tempMeshId;
				if (_primOwner.TryGetValue(prim, out tempMeshId) && (existingMeshId == null || tempMeshId == existingMeshId))
				{
					existingMeshId = tempMeshId;
				}
				else
				{
					existingMeshId = null;
					break;
				}
			}

			// if so, return that mesh id
			if (existingMeshId != null)
			{
				exportMeshMarker.End();
				return existingMeshId;
			}

			// if not, create new mesh and return its id
			var mesh = new GLTFMesh();

			if (settings.ExportNames)
			{
				mesh.Name = name;
			}

			mesh.Primitives = new List<MeshPrimitive>(uniquePrimitives.Count);
			foreach (var primKey in uniquePrimitives)
			{
				MeshPrimitive[] meshPrimitives = ExportPrimitive(primKey, mesh);
				if (meshPrimitives != null)
				{
					mesh.Primitives.AddRange(meshPrimitives);
				}
			}

			var id = new MeshId
			{
				Id = _root.Meshes.Count,
				Root = _root
			};

			exportMeshMarker.End();

			if (mesh.Primitives.Count > 0)
			{
				_root.Meshes.Add(mesh);

				var uniquePrimitive = uniquePrimitives.FirstOrDefault();
				if (uniquePrimitive.Mesh)
				{
					foreach (var plugin in _plugins)
						plugin?.AfterMeshExport(this, uniquePrimitive.Mesh, mesh, id.Id);
				}
				
				return id;
			}

			return null;
		}

		// a mesh *might* decode to multiple prims if there are submeshes
		private MeshPrimitive[] ExportPrimitive(UniquePrimitive primKey, GLTFMesh mesh)
		{
			exportPrimitiveMarker.Begin();

			Mesh meshObj = primKey.Mesh;
			Material[] materialsObj = primKey.Materials;

			var maxOfSubMeshesAndMaterials = Math.Max(meshObj.subMeshCount, materialsObj.Length);
			var prims = new MeshPrimitive[maxOfSubMeshesAndMaterials];
			
			List<MeshPrimitive> nonEmptyPrims = null;
			var vertices = meshObj.vertices;
			if (vertices.Length < 1)
			{
				Debug.LogWarning(null, "MeshFilter does not contain any vertices or they can't be accessed, won't export: " + meshObj.name, meshObj);
				exportPrimitiveMarker.End();
				return null;
			}

			if (!_meshToPrims.ContainsKey(meshObj))
			{
				AccessorId aPosition = null, aNormal = null, aTangent = null, aColor0 = null;
				var aTexcoords = new AccessorId[MaxUVChannels];

				aPosition = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(meshObj.vertices, SchemaExtensions.CoordinateSpaceConversionScale));

				if (meshObj.HasVertexAttribute(VertexAttribute.Normal))
					aNormal = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(meshObj.normals, SchemaExtensions.CoordinateSpaceConversionScale));

				if (meshObj.HasVertexAttribute(VertexAttribute.Tangent))
					aTangent = ExportAccessor(SchemaExtensions.ConvertTangentCoordinateSpaceAndCopy(meshObj.tangents, SchemaExtensions.TangentSpaceConversionScale));

				// 统一循环处理所有 UV 通道（0~7），解除 UV 数量硬编码限制
				for (int uvChannel = 0; uvChannel < MaxUVChannels; uvChannel++)
				{
					aTexcoords[uvChannel] = ExportUVChannel(meshObj, uvChannel);
				}

				if (settings.ExportVertexColors && meshObj.colors.Length != 0)
					aColor0 = ExportAccessor(QualitySettings.activeColorSpace == ColorSpace.Linear ? meshObj.colors : meshObj.colors.ToLinear(), true);

				aPosition.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
				if (aNormal != null) aNormal.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
				if (aTangent != null) aTangent.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
				for (int uvChannel = 0; uvChannel < MaxUVChannels; uvChannel++)
				{
					if (aTexcoords[uvChannel] != null)
						aTexcoords[uvChannel].Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
				}
				if (aColor0 != null) aColor0.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;

				_meshToPrims.Add(meshObj, new MeshAccessors()
				{
					aPosition = aPosition,
					aNormal = aNormal,
					aTangent = aTangent,
					aTexcoords = aTexcoords,
					aColor0 = aColor0,
					subMeshPrimitives = new Dictionary<int, MeshPrimitive>()
				});
			}

			var accessors = _meshToPrims[meshObj];

			// walk submeshes and export the ones with non-null meshes
			for (int id = 0; id < maxOfSubMeshesAndMaterials; id++)
			{
				var mat = materialsObj[id % materialsObj.Length];
				var submesh = id % meshObj.subMeshCount;
				
				if (!mat) continue;
				if (meshObj.GetIndexCount(submesh) <= 0) continue;

				if (!accessors.subMeshPrimitives.ContainsKey(submesh))
				{
					var primitive = new MeshPrimitive();

					var topology = meshObj.GetTopology(submesh);
					var indices = meshObj.GetIndices(submesh);
					if (topology == MeshTopology.Triangles) SchemaExtensions.FlipTriangleFaces(indices);

					primitive.Mode = GetDrawMode(topology);
					primitive.Indices = ExportAccessor(indices, true);
					primitive.Indices.Value.BufferView.Value.Target = BufferViewTarget.ElementArrayBuffer;

					primitive.Attributes = new Dictionary<string, AccessorId>();
					primitive.Attributes.Add(SemanticProperties.POSITION, accessors.aPosition);

					if (accessors.aNormal != null)
						primitive.Attributes.Add(SemanticProperties.NORMAL, accessors.aNormal);
					if (accessors.aTangent != null)
						primitive.Attributes.Add(SemanticProperties.TANGENT, accessors.aTangent);
					if (accessors.aTexcoords != null)
					{
						for (int uvChannel = 0; uvChannel < accessors.aTexcoords.Length; uvChannel++)
						{
							if (accessors.aTexcoords[uvChannel] != null)
								primitive.Attributes.Add(UVSemantics[uvChannel], accessors.aTexcoords[uvChannel]);
						}
					}
					if (accessors.aColor0 != null)
						primitive.Attributes.Add(SemanticProperties.COLOR_0, accessors.aColor0);

					primitive.Material = null;

					ExportBlendShapes(primKey.SkinnedMeshRenderer, meshObj, submesh, primitive, mesh);

					accessors.subMeshPrimitives.Add(submesh, primitive);
				}

				var submeshPrimitive = accessors.subMeshPrimitives[submesh];
				prims[id] = new MeshPrimitive(submeshPrimitive, _root)
				{
					Material = ExportMaterial(mat),
				};
				// this will contain only the last one
				accessors.subMeshPrimitives[submesh] = prims[submesh];
			}

            nonEmptyPrims = new List<MeshPrimitive>(prims.Length);
            for (var i = 0; i < prims.Length; i++)
            {
	            var prim = prims[i];
	            // remove any prims that have empty triangles
	            if (EmptyPrimitive(prim)) continue;
	            // invoke pre export event
	            foreach (var plugin in _plugins)
		            plugin?.AfterPrimitiveExport(this, meshObj, prim, i);
	            nonEmptyPrims.Add(prim);
            }
            prims = nonEmptyPrims.ToArray();

            exportPrimitiveMarker.End();

            return prims;
		}

		private List<double> GetBlendShapeWeights(SkinnedMeshRenderer smr, Mesh meshObj)
		{
			if (_NodeBlendShapeWeights.TryGetValue(smr, out var w))
				return w;

			List<Double> weights = new List<double>(meshObj.blendShapeCount);
			
			for (int blendShapeIndex = 0; blendShapeIndex < meshObj.blendShapeCount; blendShapeIndex++)
			{
				// We need to get the weight from the SkinnedMeshRenderer because this represents the currently
				// defined weight by the user to apply to this blend shape.  If we instead got the value from
				// the unityMesh, it would be a _per frame_ weight, and for a single-frame blend shape, that would
				// always be 100.  A blend shape might have more than one frame if a user wanted to more tightly
				// control how a blend shape will be animated during weight changes (e.g. maybe they want changes
				// between 0-50% to be really minor, but between 50-100 to be extreme, hence they'd have two frames
				// where the first frame would have a weight of 50 (meaning any weight between 0-50 should be relative
				// to the values in this frame) and then any weight between 50-100 would be relevant to the weights in
				// the second frame.  See Post 20 for more info:
				// https://forum.unity3d.com/threads/is-there-some-method-to-add-blendshape-in-editor.298002/#post-2015679
				var frameWeight = meshObj.GetBlendShapeFrameWeight(blendShapeIndex, 0);
				weights.Add(smr.GetBlendShapeWeight(blendShapeIndex) / frameWeight);
			}

			return weights;
		}
		
		// Blend Shapes / Morph Targets
		// Adopted from Gary Hsu (bghgary)
		// https://github.com/bghgary/glTF-Tools-for-Unity/blob/master/UnityProject/Assets/Gltf/Editor/Exporter.cs
		private void ExportBlendShapes(SkinnedMeshRenderer smr, Mesh meshObj, int submeshIndex, MeshPrimitive primitive, GLTFMesh mesh)
		{
			if (settings.BlendShapeExportProperties == GLTFSettings.BlendShapeExportPropertyFlags.None)
				return;

			if (_meshToBlendShapeAccessors.TryGetValue(meshObj, out var data))
			{
				primitive.Targets = data.targets;
				mesh.Weights = data.weights;
				mesh.TargetNames = data.targetNames;
				return;
			}

			if (smr != null && meshObj.blendShapeCount > 0)
			{
				List<Dictionary<string, AccessorId>> targets = new List<Dictionary<string, AccessorId>>(meshObj.blendShapeCount);
				List<Double> weights;
				List<string> targetNames = new List<string>(meshObj.blendShapeCount);

#if UNITY_2019_3_OR_NEWER
				var meshHasNormals = meshObj.HasVertexAttribute(VertexAttribute.Normal);
				var meshHasTangents = meshObj.HasVertexAttribute(VertexAttribute.Tangent);
#else
				var meshHasNormals = meshObj.normals.Length > 0;
				var meshHasTangents = meshObj.tangents.Length > 0;
#endif

				for (int blendShapeIndex = 0; blendShapeIndex < meshObj.blendShapeCount; blendShapeIndex++)
				{
					exportBlendShapeMarker.Begin();

					targetNames.Add(meshObj.GetBlendShapeName(blendShapeIndex));
					// As described above, a blend shape can have multiple frames.  Given that glTF only supports a single frame
					// per blend shape, we'll always use the final frame (the one that would be for when 100% weight is applied).
					int frameIndex = meshObj.GetBlendShapeFrameCount(blendShapeIndex) - 1;

					var deltaVertices = new Vector3[meshObj.vertexCount];
					var deltaNormals = new Vector3[meshObj.vertexCount];
					var deltaTangents = new Vector3[meshObj.vertexCount];
					meshObj.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);

					var exportTargets = new Dictionary<string, AccessorId>();

					if (!settings.BlendShapeExportSparseAccessors)
					{
						var positionAccessor = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaVertices, SchemaExtensions.CoordinateSpaceConversionScale));
						positionAccessor.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
						exportTargets.Add(SemanticProperties.POSITION, positionAccessor);
					}
					else
					{
						// Debug.Log("Delta Vertices:\n"+string.Join("\n ", deltaVertices));
						// Debug.Log("Vertices:\n"+string.Join("\n ", meshObj.vertices));
						// Experimental: sparse accessor.
						// - get the accessor we want to base this upon
						// - this is how position is originally exported:
						//   ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(meshObj.vertices, SchemaExtensions.CoordinateSpaceConversionScale));
						var exportedAccessor = ExportSparseAccessor(null, null, SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaVertices, SchemaExtensions.CoordinateSpaceConversionScale));
						if (exportedAccessor != null)
						{
							exportTargets.Add(SemanticProperties.POSITION, exportedAccessor);
						}
					}

					if (meshHasNormals && settings.BlendShapeExportProperties.HasFlag(GLTFSettings.BlendShapeExportPropertyFlags.Normal))
					{
						if (!settings.BlendShapeExportSparseAccessors)
						{
							var accessor = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaNormals, SchemaExtensions.CoordinateSpaceConversionScale));
							accessor.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
							exportTargets.Add(SemanticProperties.NORMAL, accessor);
						}
						else
						{
							exportTargets.Add(SemanticProperties.NORMAL, ExportSparseAccessor(null, null, SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaNormals, SchemaExtensions.CoordinateSpaceConversionScale)));
						}
					}
					if (meshHasTangents && settings.BlendShapeExportProperties.HasFlag(GLTFSettings.BlendShapeExportPropertyFlags.Tangent))
					{
						if (!settings.BlendShapeExportSparseAccessors)
						{
							var accessor = ExportAccessor(SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaTangents, SchemaExtensions.CoordinateSpaceConversionScale));
							accessor.Value.BufferView.Value.Target = BufferViewTarget.ArrayBuffer;
							exportTargets.Add(SemanticProperties.TANGENT, accessor);
						}
						else
						{
							exportTargets.Add(SemanticProperties.TANGENT, ExportSparseAccessor(null, null, SchemaExtensions.ConvertVector3CoordinateSpaceAndCopy(deltaTangents, SchemaExtensions.CoordinateSpaceConversionScale)));
						}
					}

					targets.Add(exportTargets);
					
					exportBlendShapeMarker.End();
				}

				weights = GetBlendShapeWeights(smr, meshObj);
				if(weights.Any() && targets.Any())
				{
					mesh.Weights = weights;
					mesh.TargetNames = targetNames;
					primitive.Targets = targets;
					_NodeBlendShapeWeights.Add(smr, weights);
				}
				else
				{
					mesh.Weights = null;
					mesh.TargetNames = null;
					primitive.Targets = null;
				}

				// cache the exported data; we can re-use it between all submeshes of a mesh.
				_meshToBlendShapeAccessors.Add(meshObj, new BlendShapeAccessors()
				{
					targets = targets,
					weights = weights,
					targetNames = targetNames,
					firstSkinnedMeshRenderer = smr
				});
			}
		}

		private static bool EmptyPrimitive(MeshPrimitive prim)
		{
			if (prim == null || prim.Attributes == null)
			{
				return true;
			}
			return false;
		}

		/// <summary>
		/// 导出单个 UV 通道为 AccessorId。
		///
		/// ▼▼▼ V 翻转策略（UV 原点从 Unity 左上 → glTF 左下）▼▼▼
		///   UV0           : 始终翻转 V（glTF 规范对基础贴图 UV 的硬约定）。
		///   UV1           : 由 <see cref="GLTFSettings.FlipTexCoord1V"/> 控制。
		///                   - 默认 false（不翻）：适用于 Houdini/程序化管线把位置、方向、mask
		///                     等数据烘进 UV1 的场景（翻转会把数值破坏）。
		///                   - 设为 true（翻转）：适用于 UV1 被当作 lightmap UV 或第二套贴图 UV
		///                     的传统场景。
		///                   ⚠ 未来如果项目里 UV1 重新给 lightmap 使用，请务必：
		///                     1) 在 GLTFSettings 里把 FlipTexCoord1V 勾上；
		///                     2) 确保目标网格的 UV1 只用来做贴图采样，不要再混入数据。
		///   UV2 ~ UV7     : 始终不翻转，作为任意维度的自定义顶点属性原样透传
		///                   （glTF 规范允许 TEXCOORD_n 有任意数量的分量：vec2/vec3/vec4）。
		/// 返回 null 表示该通道不存在或为空。
		/// </summary>
		private AccessorId ExportUVChannel(Mesh meshObj, int uvChannel)
		{
			if (uvChannel < 0 || uvChannel >= MaxUVChannels) return null;

			var attr = UVAttributes[uvChannel];
			if (!meshObj.HasVertexAttribute(attr)) return null;

			var dim = meshObj.GetVertexAttributeDimension(attr);

			// 判定该通道是否需要 V 翻转
			bool flipV;
			if (uvChannel == 0)
			{
				flipV = true; // UV0：glTF 规范强制
			}
			else if (uvChannel == 1)
			{
				// UV1：按开关决定。默认 false（即不翻转，保证数据通道）。
				flipV = settings != null && settings.FlipTexCoord1V;
			}
			else
			{
				flipV = false; // UV2~UV7：自定义数据通道，始终不翻
			}

			// 翻转路径只对 Vector2 有意义（V 翻转本质是对 y 做 1-y）
			if (flipV)
			{
				if (dim != 2)
				{
					Debug.LogWarning(null,
						$"UV{uvChannel} 被标记为需要 V 翻转（贴图 UV 语义），但维度为 {dim}。" +
						$"glTF 贴图 UV 只支持 Vector2，将仅导出 xy 并执行 V 翻转。Mesh: {meshObj.name}");
				}
				var uvs = new List<Vector2>(meshObj.vertexCount);
				meshObj.GetUVs(uvChannel, uvs);
				if (uvs.Count == 0) return null;
				return ExportAccessor(SchemaExtensions.FlipTexCoordArrayVAndCopy(uvs.ToArray()));
			}

			// 不翻转路径：按实际维度原样导出（支持 vec2/vec3/vec4）
			if (dim == 2)
			{
				var uvs = new List<Vector2>(meshObj.vertexCount);
				meshObj.GetUVs(uvChannel, uvs);
				if (uvs.Count == 0) return null;
				return ExportAccessor(uvs.ToArray());
			}
			else if (dim == 3)
			{
				var uvs = new List<Vector3>(meshObj.vertexCount);
				meshObj.GetUVs(uvChannel, uvs);
				if (uvs.Count == 0) return null;
				return ExportAccessor(uvs.ToArray());
			}
			else if (dim == 4)
			{
				var uvs = new List<Vector4>(meshObj.vertexCount);
				meshObj.GetUVs(uvChannel, uvs);
				if (uvs.Count == 0) return null;
				return ExportAccessor(uvs.ToArray());
			}
			return null;
		}

		private static DrawMode GetDrawMode(MeshTopology topology)
		{
			switch (topology)
			{
				case MeshTopology.Points: return DrawMode.Points;
				case MeshTopology.Lines: return DrawMode.Lines;
				case MeshTopology.LineStrip: return DrawMode.LineStrip;
				case MeshTopology.Triangles: return DrawMode.Triangles;
			}

			throw new Exception("glTF does not support Unity mesh topology: " + topology);
		}

#if UNITY_EDITOR
		private const string MakeMeshReadableDialogueDecisionKey = nameof(MakeMeshReadableDialogueDecisionKey);
		private static PropertyInfo canAccessProperty =
			typeof(Mesh).GetProperty("canAccess", BindingFlags.Instance | BindingFlags.Default | BindingFlags.NonPublic);
#endif

		private static bool MeshIsReadable(Mesh mesh)
		{
#if UNITY_EDITOR
			return mesh.isReadable || (bool) (canAccessProperty?.GetMethod?.Invoke(mesh, null) ?? true);
#else
			return mesh.isReadable;
#endif
		}
	}
}
