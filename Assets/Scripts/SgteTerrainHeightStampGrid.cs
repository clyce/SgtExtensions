using System;
using System.Collections;
using System.Collections.Generic;
using CW.Common;
using SpaceGraphicsToolkit;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;


namespace SgtExtensions {

    [ExecuteInEditMode]
    [RequireComponent(typeof(SgtTerrain))]
    public class SgteTerrainHeightStampGrid : MonoBehaviour {
        public uint Seed { set { if (seed != value) { seed = value; MarkAsDirty(); } } get { return seed; } }
        [SerializeField] private uint seed;
        public int TilesPerDim { set { if (tilesPerDim != value) { tilesPerDim = value; MarkAsDirty(); } } get { return tilesPerDim; } }
        [SerializeField] private int tilesPerDim = 1;
        public float TileSpan { set { if (tileSpan != value) { tileSpan = value; MarkAsDirty(); } } get { return tileSpan; } }
        [SerializeField] private float tileSpan = 0.125f;
        public int Area { set { area = value; MarkAsDirty(); } get { return area; } }
        [SerializeField] private int area = -1;
        public double Displacement { set { if (displacement != value) { displacement = value; MarkAsDirty(); } } get { return displacement; } }
        [SerializeField] private double displacement = 0.25;
        public double HeightOrigin { set { if (heightOrigin != value) { heightOrigin = value; MarkAsDirty(); } } get { return heightOrigin; } }
        [SerializeField] private double heightOrigin = 0.5;

        // currently support only single-channel
        [Serializable]
        public class HeightStampCfg {
            public Texture2D stampMap;
            [Range(0, 1)] public double localHeightOrigin = .5f;
            [Range(-1, 1)] public double localDisplacement = 1f;
        }

        public HeightStampCfg[] HeightStamps { set { if (heightStamps != value) { heightStamps = value; PrepareTextures(); } } get { return heightStamps; } }
        [SerializeField] private HeightStampCfg[] heightStamps;
        private SgtTerrain cachedTerrain;
        private NativeArray<byte> cachedHeightStampData = new NativeArray<byte>();
        private NativeArray<float> tempWeights;
        private NativeArray<int> tileMappings = new NativeArray<int>();
        private NativeArray<bool3> tileFlipAndTranspose = new NativeArray<bool3>();
        private NativeArray<double> localHeightOrigins = new NativeArray<double>();
        private NativeArray<double> localDisplacements = new NativeArray<double>();
        private int stampCount => heightStamps.Length;
        public void MarkAsDirty() {
            if (heightStamps.Length > 0) {
                try { tileMappings.Dispose(); } catch { };
                try { tileFlipAndTranspose.Dispose(); } catch { }
                int totalTiles = TilesPerDim * TilesPerDim * 6;
                tileMappings = new NativeArray<int>(totalTiles, Allocator.Persistent);
                tileFlipAndTranspose = new NativeArray<bool3>(totalTiles, Allocator.Persistent);
                var rand = Unity.Mathematics.Random.CreateFromIndex(Seed);
                for (int i = 0; i < tileMappings.Length; i++) {
                    tileMappings[i] = rand.NextInt(0, heightStamps.Length);
                    tileFlipAndTranspose[i] = rand.NextBool3();
                }
            }
            if (cachedTerrain != null) {
                cachedTerrain.MarkAsDirty();
            }
        }

        public void PrepareTextures() {
            try { cachedHeightStampData.Dispose(); } catch { }
            try { localHeightOrigins.Dispose(); } catch { }
            try { localDisplacements.Dispose(); } catch { }
            NativeList<byte> heightStampDataList = new NativeList<byte>(Allocator.Persistent);
            localHeightOrigins = new NativeArray<double>(heightStamps.Length, Allocator.Persistent);
            localDisplacements = new NativeArray<double>(heightStamps.Length, Allocator.Persistent);
            for (int i = 0; i < heightStamps.Length; i++) {
                heightStampDataList.AddRange(heightStamps[i].stampMap.GetRawTextureData<byte>());
                localHeightOrigins[i] = heightStamps[i].localHeightOrigin;
                localDisplacements[i] = heightStamps[i].localDisplacement;
            }
            cachedHeightStampData = heightStampDataList.AsArray();
            MarkAsDirty();
        }

        protected virtual void OnEnable() {
            cachedTerrain = GetComponent<SgtTerrain>();

            cachedTerrain.OnScheduleHeights += HandleScheduleHeights;
            cachedTerrain.OnScheduleCombinedHeights += HandleScheduleHeights;

            tempWeights = new NativeArray<float>(0, Allocator.Persistent);
            PrepareTextures();
        }

        protected virtual void OnDisable() {
            cachedTerrain.OnScheduleHeights -= HandleScheduleHeights;
            cachedTerrain.OnScheduleCombinedHeights -= HandleScheduleHeights;

            cachedTerrain.ScheduleDispose(cachedHeightStampData);
            cachedTerrain.ScheduleDispose(tempWeights);
            cachedTerrain.ScheduleDispose(tileMappings);
            cachedTerrain.ScheduleDispose(localDisplacements);
            cachedTerrain.ScheduleDispose(localHeightOrigins);
            cachedTerrain.ScheduleDispose(tileFlipAndTranspose);
            cachedTerrain.MarkAsDirty();
        }

#if UNITY_EDITOR
        protected virtual void OnValidate() {
            MarkAsDirty();
        }
#endif

        protected virtual void OnDidApplyAnimationProperties() {
            MarkAsDirty();
        }


        [BurstCompile]
        public static (int, double2) pointToCubeQuadIdAndUV(double3 point) {
            var abs = math.abs(point);
            if (abs.x >= abs.y) { // X > Y
                if (abs.x >= abs.z) { // X > Y & Z
                    return point.x > 0
                        ? (2, new double2(-point.z, point.y) * .5f + .5f)
                        : (0, new double2(point.z, point.y) * .5f + .5f);
                }
            } else { // Y > X
                if (abs.y >= abs.z) { // Y > X & Z
                    return point.y > 0
                        ? (4, new double2(point.x, -point.z) * .5f + .5f)
                        : (5, new double2(point.x, point.z) * .5f + .5f);
                }
            }
            // z
            return point.z > 0
                ? (1, new double2(point.x, point.y) * .5f + .5f)
                : (3, new double2(-point.x, point.y) * .5f + .5f);
        }

        // id, left, right, up, down
        private void HandleScheduleHeights(NativeArray<double3> points, NativeArray<double> heights, ref JobHandle handle) {
            if (heightStamps.Length <= 0) return;
            var job = new HeightsJob();
            job.Displacement = 1000;
            job.Seed = Seed;
            job.Displacement = Displacement;
            job.HeightOrigin = HeightOrigin;
            var sampleTex = heightStamps[0].stampMap;
            job.StampSize = new int2(sampleTex.width, sampleTex.height);
            job.StampStride = SgtCommon.GetStride(sampleTex.format);
            job.StampOffset = SgtCommon.GetOffset(sampleTex.format, 0);
            job.TilesPerDim = TilesPerDim;
            job.TilesPerQuad = TilesPerDim * TilesPerDim;
            job.TileSpan = TileSpan;
            job.TileMapping = tileMappings;
            job.TileFlipAndTranspose = tileFlipAndTranspose;
            job.StampCount = stampCount;
            job.StampBSize = cachedHeightStampData.Length / stampCount;
            job.LocalDisplacements = localDisplacements;
            job.LocalHeightOrigins = localHeightOrigins;
            job.Points = points;
            job.HeightArray = cachedHeightStampData;
            job.Heights = heights;

            if (cachedTerrain.Areas != null && cachedTerrain.Areas.SplatCount > 0 && Area >= 0) {
                job.Area = math.clamp(area, 0, cachedTerrain.Areas.SplatCount - 1);
                job.AreaSize = cachedTerrain.Areas.Size;
                job.AreaSplatCount = cachedTerrain.Areas.SplatCount;
                job.AreaWeights = cachedTerrain.Areas.Weights;
            } else {
                job.Area = 0;
                job.AreaSize = int2.zero;
                job.AreaSplatCount = 0;
                job.AreaWeights = tempWeights;
            }

            handle = job.Schedule(heights.Length, 32, handle);
        }

        [BurstCompile]
        public struct HeightsJob : IJobParallelFor {
            public uint Seed;
            [ReadOnly] public NativeArray<double3> Points;
            [ReadOnly] public NativeArray<byte> HeightArray;
            [ReadOnly] public NativeArray<int> TileMapping;
            [ReadOnly] public NativeArray<bool3> TileFlipAndTranspose;
            [ReadOnly] public NativeArray<double> LocalHeightOrigins;
            [ReadOnly] public NativeArray<double> LocalDisplacements;
            public double Displacement, HeightOrigin;
            public int2 StampSize;
            public int StampStride, StampOffset;
            public int TilesPerDim;
            public float TileSpan;
            public int StampCount;
            public int StampBSize;
            public int TilesPerQuad;

            [ReadOnly] public int Area;
            [ReadOnly] public int2 AreaSize;
            [ReadOnly] public int AreaSplatCount;
            [ReadOnly] public NativeArray<float> AreaWeights;

            public NativeArray<double> Heights;
            [BurstCompile]
            public int TileIdOf(int quadId, int x, int y) {
                return CoordUtils.IntPosToIndex(
                    x >= 0 ? x : TilesPerDim + x,
                    y >= 0 ? y : TilesPerDim + y,
                    TilesPerDim) + (quadId * TilesPerQuad);
            }

            [BurstCompile]
            public double GetSubHeight(double alpha, (int, int, int, double2) tileIdAnduv) {
                int tileId = TileIdOf(tileIdAnduv.Item1, tileIdAnduv.Item2, tileIdAnduv.Item3);
                double2 uv = tileIdAnduv.Item4;
                //tileId = tileId - (tileId / TileMapping.Length) * TileMapping.Length; // Just in case...
                int stampId = TileMapping[tileId];
                var flipAndTranspose = TileFlipAndTranspose[tileId];
                if (flipAndTranspose.x) uv.x = 1 - uv.x;
                if (flipAndTranspose.y) uv.y = 1 - uv.y;
                if (flipAndTranspose.z) uv = uv.yx;
                return
                    alpha * Displacement * LocalDisplacements[stampId]
                    * (SgtTerrainTopology.Sample_Cubic(
                            HeightArray.GetSubArray(stampId * StampBSize, StampBSize),
                            StampStride, StampOffset, StampSize, uv)
                       - LocalHeightOrigins[stampId] - HeightOrigin);
            }

            /*
            Cubemap QuadIdx:
                    _________
                    | +Y(4) |
             _______|_______|________________
            | -x(0) | +z(1) | +x(2) | -z(3) |
            |_______|_______|_______|_______|
                    | -Y(5) |
                    |_______|
            */

            // return: quadIdx, localTileY, localTileY, localUV
            [BurstCompile]
            public (int, int, int, double2) LeftNeighbourTileIdAndUV(int quadIdx, int2 localTile, double2 localUV) {
                var defaultUV = new double2(localUV.x + 1 - TileSpan, localUV.y);
                switch (quadIdx) {
                    case 0:
                        return localTile.x > 0
                            ? (
                                0, localTile.x - 1, localTile.y, defaultUV
                            ) : (
                                3, -1, localTile.y, defaultUV
                            );
                    case 1:
                        return localTile.x > 0
                            ? (
                                1, localTile.x - 1, localTile.y, defaultUV
                            ) : (
                                0, -1, localTile.y, defaultUV
                            );
                    case 2:
                        return localTile.x > 0
                            ? (
                                2, localTile.x - 1, localTile.y, defaultUV
                            ) : (
                                1, -1, localTile.y, defaultUV
                            );
                    case 3:
                        return localTile.x > 0
                            ? (
                                3, localTile.x - 1, localTile.y, defaultUV
                            ) : (
                                2, -1, localTile.y, defaultUV
                            );
                    case 4:
                        return localTile.x > 0
                            ? (
                                4, localTile.x - 1, localTile.y, defaultUV
                            ) : (
                                0, -1 - localTile.y, -1,
                                new double2(1 - localUV.y, 1 - TileSpan + localUV.x)
                            );
                    case 5:
                        return localTile.x > 0
                            ? (
                                5, localTile.x - 1, localTile.y, defaultUV
                            ) : (
                                0, localTile.y, 0,
                                new double2(localUV.y, TileSpan - localUV.x)
                            );
                    default:
                        return (0, 0, 0, double2.zero);
                }
            }

            [BurstCompile]
            public (int, int, int, double2) RightNeighbourTileIdAndUV(int quadIdx, int2 localTile, double2 localUV) {
                var defaultUV = new double2(localUV.x - 1 + TileSpan, localUV.y);
                switch (quadIdx) {
                    case 0:
                        return localTile.x < TilesPerDim - 1
                            ? (
                                0, localTile.x + 1, localTile.y, defaultUV
                            ) : (
                                1, 0, localTile.y, defaultUV
                            );
                    case 1:
                        return localTile.x < TilesPerDim - 1
                            ? (
                                1, localTile.x + 1, localTile.y, defaultUV
                            ) : (
                                2, 0, localTile.y, defaultUV
                            );
                    case 2:
                        return localTile.x < TilesPerDim - 1
                            ? (
                                2, localTile.x + 1, localTile.y, defaultUV
                            ) : (
                                3, 0, localTile.y, defaultUV
                            );
                    case 3:
                        return localTile.x < TilesPerDim - 1
                            ? (
                                3, localTile.x + 1, localTile.y, defaultUV
                            ) : (
                                0, 0, localTile.y, defaultUV
                            );
                    case 4:
                        return localTile.x < TilesPerDim - 1
                            ? (
                                4, localTile.x + 1, localTile.y, defaultUV
                            ) : (
                                2, localTile.y, -1,
                                new double2(localUV.y, 2 - TileSpan - localUV.x)
                            );
                    case 5:
                        return localTile.x < TilesPerDim - 1
                            ? (
                                5, localTile.x + 1, localTile.y, defaultUV
                            ) : (
                                2, -1 - localTile.y, 0,
                                new double2(1 - localUV.y, localUV.x - 1 + TileSpan)
                            );
                    default:
                        return (0, 0, 0, double2.zero);
                }
            }

            [BurstCompile]
            public (int, int, int, double2) UpNeighbourTileIdAndUV(int quadIdx, int2 localTile, double2 localUV) {
                var defaultUV = new double2(localUV.x, localUV.y - 1 + TileSpan);
                switch (quadIdx) {
                    case 0:
                        return localTile.y < TilesPerDim - 1
                            ? (
                                0, localTile.x, localTile.y + 1, defaultUV
                            ) : (
                                4, 0, -1 - localTile.x,
                                new double2(TileSpan - 1 + localUV.y, 1 - localUV.x)
                            );
                    case 1:
                        return localTile.y < TilesPerDim - 1
                            ? (
                                1, localTile.x, localTile.y + 1, defaultUV
                            ) : (
                                4, localTile.x, 0, defaultUV
                            );
                    case 2:
                        return localTile.y < TilesPerDim - 1
                            ? (
                                2, localTile.x, localTile.y + 1, defaultUV
                            ) : (
                                4, -1, localTile.x,
                                new double2(2 - localUV.y - TileSpan, localUV.x)
                            );
                    case 3:
                        return localTile.y < TilesPerDim - 1
                            ? (
                                3, localTile.x, localTile.y + 1, defaultUV
                            ) : (
                                4, -1 - localTile.x, -1,
                                new double2(1 - localUV.x, 2 - TileSpan - localUV.y)
                            );
                    case 4:
                        return localTile.y < TilesPerDim - 1
                            ? (
                                4, localTile.x, localTile.y + 1, defaultUV
                            ) : (
                                3, -1 - localTile.x, -1,
                                new double2(1 - localUV.x, 2 - TileSpan - localUV.y)
                            );
                    case 5:
                        return localTile.y < TilesPerDim - 1
                            ? (
                                5, localTile.x, localTile.y + 1, defaultUV
                            ) : (
                                1, localTile.x, 0, defaultUV
                            );
                    default:
                        return (0, 0, 0, double2.zero);
                }
            }

            [BurstCompile]
            public (int, int, int, double2) DownNeighbourTileIdAndUV(int quadIdx, int2 localTile, double2 localUV) {
                var defaultUV = new double2(localUV.x, localUV.y + 1 - TileSpan);
                switch (quadIdx) {
                    case 0:
                        return localTile.y > 0
                            ? (
                                0, localTile.x, localTile.y - 1, defaultUV
                            ) : (
                                5, 0, localTile.x,
                                new double2(TileSpan - localUV.y, localUV.x)
                            );
                    case 1:
                        return localTile.y > 0
                            ? (
                                1, localTile.x, localTile.y - 1, defaultUV
                            ) : (
                                5, localTile.x, -1, defaultUV
                            );
                    case 2:
                        return localTile.y > 0
                            ? (
                                2, localTile.x, localTile.y - 1, defaultUV
                            ) : (
                                5, -1, -1 - localTile.x,
                                new double2(1 - TileSpan + localUV.y, 1 - localUV.x)
                            );
                    case 3:
                        return localTile.y > 0
                            ? (
                                3, localTile.x, localTile.y - 1, defaultUV
                            ) : (
                                5, -1 - localTile.x, 0,
                                new double2(1 - localUV.x, TileSpan - localUV.y)
                            );
                    case 4:
                        return localTile.y > 0
                            ? (
                                4, localTile.x, localTile.y - 1, defaultUV
                            ) : (
                                1, localTile.x, -1, defaultUV
                            );
                    case 5:
                        return localTile.y > 0
                            ? (
                                5, localTile.x, localTile.y - 1, defaultUV
                            ) : (
                                3, -1 - localTile.x, 0,
                                new double2(1 - localUV.x, TileSpan - localUV.y)
                            );
                    default:
                        return (0, 0, 0, double2.zero);
                }
            }

            public void Execute(int i) {
                if (double.IsNegativeInfinity(Heights[i]) == false && !double.IsNaN(Points[i].x)) {

                    var weight = 1.0f;

                    if (AreaWeights.Length > 0) {
                        weight = SgtTerrainTopology.Sample_Cubic_Equirectangular(AreaWeights, AreaSplatCount, Area, AreaSize, Points[i]);
                        weight = math.clamp(20000.0f - weight, 0.0f, 20000.0f) / 20000.0f;
                    }

                    if (weight > 0.0f) {

                        //double3 point = SgtTerrainTopology.Unwarp(SgtTerrainTopology.Untilt(Points[i])); deprecated since it does not fit as range [-1, 1]
                        double3 point = SgtTerrainTopology.VectorToUnitCube(Points[i]);
                        (int, double2) t = pointToCubeQuadIdAndUV(point);
                        int quadIdx = t.Item1;
                        double2 localUV = t.Item2;
                        localUV *= TilesPerDim;
                        int2 localTile = new int2(math.floor(localUV));

                        localUV = math.frac(localUV) * (1 - TileSpan) + TileSpan * .5f;

                        int tilesPerQuad = TilesPerDim * TilesPerDim;

                        // tileId, localUV
                        double alpha_K = 1 / TileSpan;
                        double2 alphaUV = math.smoothstep(0, 1, math.saturate(alpha_K * localUV));
                        double2 alphaUVMinus = math.smoothstep(0, 1, math.saturate(alpha_K * (1 - localUV)));

                        double leftAlpha = (1 - alphaUV.x);
                        double rightAlpha = (1 - alphaUVMinus.x);
                        double upAlpha = (1 - alphaUVMinus.y);
                        double downAlpha = (1 - alphaUV.y);

                        double selfAlpha =
                            (1 - leftAlpha) * (1 - rightAlpha)
                            * (1 - upAlpha) * (1 - downAlpha);

                        double localHeight =
                            GetSubHeight(
                                selfAlpha, (quadIdx, localTile.x, localTile.y, localUV));

                        if (leftAlpha > 0) {
                            var leftNeighbourTileIdAndUV =
                                LeftNeighbourTileIdAndUV(quadIdx, localTile, localUV);
                            if (upAlpha > 0) { // up-left corner
                                localHeight += GetSubHeight(
                                    leftAlpha * (1 - upAlpha),
                                    leftNeighbourTileIdAndUV);

                                localHeight += GetSubHeight(
                                    (1 - leftAlpha) * upAlpha,
                                    UpNeighbourTileIdAndUV(quadIdx, localTile, localUV));

                                localHeight += GetSubHeight(
                                    leftAlpha * upAlpha,
                                    UpNeighbourTileIdAndUV(
                                        leftNeighbourTileIdAndUV.Item1,
                                        new int2(leftNeighbourTileIdAndUV.Item2, leftNeighbourTileIdAndUV.Item3),
                                        leftNeighbourTileIdAndUV.Item4));
                            } else if (downAlpha > 0) { // down-left corner
                                localHeight += GetSubHeight(
                                    leftAlpha * (1 - downAlpha),
                                    leftNeighbourTileIdAndUV);

                                localHeight += GetSubHeight(
                                    (1 - leftAlpha) * downAlpha,
                                    DownNeighbourTileIdAndUV(quadIdx, localTile, localUV));

                                localHeight += GetSubHeight(
                                    leftAlpha * downAlpha,
                                    DownNeighbourTileIdAndUV(
                                        leftNeighbourTileIdAndUV.Item1,
                                        new int2(leftNeighbourTileIdAndUV.Item2, leftNeighbourTileIdAndUV.Item3),
                                        leftNeighbourTileIdAndUV.Item4));

                            } else {
                                localHeight += GetSubHeight(
                                    leftAlpha, leftNeighbourTileIdAndUV);
                            }
                        } else if (rightAlpha > 0) {
                            var rightNeighbourTileIdAndUV =
                                RightNeighbourTileIdAndUV(quadIdx, localTile, localUV);
                            if (upAlpha > 0) { // up-left corner
                                localHeight += GetSubHeight(
                                    rightAlpha * (1 - upAlpha),
                                    rightNeighbourTileIdAndUV);

                                localHeight += GetSubHeight(
                                    (1 - rightAlpha) * upAlpha,
                                    UpNeighbourTileIdAndUV(quadIdx, localTile, localUV));

                                localHeight += GetSubHeight(
                                    rightAlpha * upAlpha,
                                    UpNeighbourTileIdAndUV(
                                        rightNeighbourTileIdAndUV.Item1,
                                        new int2(rightNeighbourTileIdAndUV.Item2, rightNeighbourTileIdAndUV.Item3),
                                        rightNeighbourTileIdAndUV.Item4));

                            } else if (downAlpha > 0) { // down-left corner
                                localHeight += GetSubHeight(
                                    rightAlpha * (1 - downAlpha),
                                    rightNeighbourTileIdAndUV);

                                localHeight += GetSubHeight(
                                    (1 - rightAlpha) * downAlpha,
                                    DownNeighbourTileIdAndUV(quadIdx, localTile, localUV));

                                localHeight += GetSubHeight(
                                    rightAlpha * downAlpha,
                                    DownNeighbourTileIdAndUV(
                                        rightNeighbourTileIdAndUV.Item1,
                                        new int2(rightNeighbourTileIdAndUV.Item2, rightNeighbourTileIdAndUV.Item3),
                                        rightNeighbourTileIdAndUV.Item4));

                            } else {
                                localHeight += GetSubHeight(
                                    rightAlpha, rightNeighbourTileIdAndUV);
                            }
                        } else if (upAlpha > 0) {
                            localHeight += GetSubHeight(
                                upAlpha,
                                UpNeighbourTileIdAndUV(quadIdx, localTile, localUV));
                        } else if (downAlpha > 0) {
                            localHeight += GetSubHeight(
                                downAlpha,
                                DownNeighbourTileIdAndUV(quadIdx, localTile, localUV));
                        }
                        Heights[i] += localHeight * weight;
                    }
                }
            }
        }
    }

}


#if UNITY_EDITOR

namespace SgtExtensions {

    using UnityEditor;
    using TARGET = SgteTerrainHeightStampGrid;

    [CanEditMultipleObjects]
    [CustomEditor(typeof(SgteTerrainHeightStampGrid))]
    public class PlanetTerrainStamps_Editor : CwEditor {
        protected override void OnInspector() {
            TARGET tgt; TARGET[] tgts; GetTargets(out tgt, out tgts);

            var markAsDirty = false;
            var prepareTextures = false;

            markAsDirty |= SgtTerrain_Editor.DrawArea(serializedObject.FindProperty("area"), tgt.GetComponent<SgtTerrain>());

            Separator();

            Draw("seed", ref markAsDirty, "The random seed of distributing the stamps");
            Draw("displacement", ref markAsDirty, "The random seed of distributing the stamps");
            Draw("heightOrigin", ref markAsDirty, "The origin point of the displacement with respect to the pixel value");

            BeginError(Any(tgts, t => t.TilesPerDim == 0));
            Draw("tilesPerDim", ref markAsDirty, "Number of the tiles per dimension on each cube face");
            EndError();

            BeginError(Any(tgts, t => t.TileSpan < 0 || t.TileSpan > 1));
            Draw("tileSpan", ref markAsDirty, "The span of each tile for the lerp");
            EndError();

            BeginError(Any(tgts, t => t.HeightStamps.Length == 0));
            Draw("heightStamps", ref prepareTextures, "Number of the tiles per dimension on each cube face");
            EndError();

            if (prepareTextures == true) {
                Each(tgts, t => t.PrepareTextures(), true, true);
            }

            if (markAsDirty == true) {
                Each(tgts, t => t.MarkAsDirty(), true, true);
            }
        }
    }

}
#endif