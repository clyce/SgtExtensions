using System;
using System.Collections;
using System.Collections.Generic;
using SpaceGraphicsToolkit;
using UnityEngine;

namespace SgtExtensions {

    [Serializable]
    [CreateAssetMenu(menuName = "SgtExtension/Terrain Height Stamps")]
    public class SgteTerrainHeightStamps : ScriptableObject {
        // currently support only single-channel textures on alpha channel, with equal sizes
        [Serializable]
        public class HeightStampCfg {
            public Texture2D stampMap;
            [Range(0, 1)] public double localHeightOrigin = .5f;
            [Range(-1, 1)] public double localDisplacement = 1f;
        }

        public List<HeightStampCfg> heightStampConfigs;

        public enum HeightOriginMethod {
            Min = 0,
            Average = 1,
            Max = 2,
        }
        public void AutoHeightOrigin(HeightOriginMethod heightOriginMethod) {
            heightStampConfigs.ForEach(
                x => {
                    var pixels = x.stampMap.GetPixels();
                    float _texOrigin;
                    switch (heightOriginMethod) {
                        case HeightOriginMethod.Min:
                            _texOrigin = 1;
                            foreach (var color in pixels) {
                                if (_texOrigin > color.a) {
                                    _texOrigin = color.a;
                                }
                            }
                            break;
                        case HeightOriginMethod.Average:
                            _texOrigin = 0;
                            foreach (var color in pixels) {
                                _texOrigin += color.a;
                            }
                            _texOrigin /= pixels.Length;
                            break;
                        case HeightOriginMethod.Max:
                            _texOrigin = 0;
                            foreach (var color in pixels) {
                                if (_texOrigin < color.a) {
                                    _texOrigin = color.a;
                                }
                            }
                            break;
                        default:
                            _texOrigin = 0;
                            break;
                    }
                    x.localHeightOrigin = _texOrigin;
                }
            );
        }
    }
}

#if UNITY_EDITOR
namespace SgtExtensions {

    using CW.Common;
    using UnityEditor;
    using TARGET = SgteTerrainHeightStamps;


    [CustomEditor(typeof(SgteTerrainHeightStamps))]
    public class SgteTerrainHeightStamps_Editor : CwEditor {

        protected override void OnInspector() {
            TARGET tgt; TARGET[] tgts; GetTargets(out tgt, out tgts);


            BeginError(Any(tgts, t => t.heightStampConfigs.Count<= 0));
            Draw("heightStampConfigs", "The height stamps library used for this drawing stamps from");
            EndError();

            if (Button("Auto HeightOrigin by Min")) {
                tgt.AutoHeightOrigin(TARGET.HeightOriginMethod.Min);
                DirtyAndUpdate();
            }
            if (Button("Auto HeightOrigin by Average")) {
                tgt.AutoHeightOrigin(TARGET.HeightOriginMethod.Average);
                DirtyAndUpdate();
            }
            if (Button("Auto HeightOrigin by Max")) {
                tgt.AutoHeightOrigin(TARGET.HeightOriginMethod.Max);
                DirtyAndUpdate();
            }
        }

    }
}
#endif