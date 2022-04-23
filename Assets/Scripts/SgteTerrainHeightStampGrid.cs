using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
        public uint Seed { set { if (seed != value) { seed = value; RefreshHeightStampsCache(); } } get { return seed; } }
        [SerializeField] private uint seed;
        public int GridsPerDim { set { if (gridsPerDim != value) { gridsPerDim = value; MarkAsDirty(); } } get { return gridsPerDim; } }
        [SerializeField] private int gridsPerDim = 1;
        public float GridSpan { set { if (gridSpan != value) { gridSpan = value; MarkAsDirty(); } } get { return gridSpan; } }
        [SerializeField] private float gridSpan = 0.125f;
        public int Area { set { area = value; MarkAsDirty(); } get { return area; } }
        [SerializeField] private int area = -1;
        public double Displacement { set { if (displacement != value) { displacement = value; MarkAsDirty(); } } get { return displacement; } }
        [SerializeField] private double displacement = 0.25;
        public double HeightOrigin { set { if (heightOrigin != value) { heightOrigin = value; MarkAsDirty(); } } get { return heightOrigin; } }
        [SerializeField] private double heightOrigin = 0.5;
        public SgteTerrainHeightStamps HeightStamps { set { if (heightStamps != value) { heightStamps = value; RefreshHeightStampsCache(); } } get { return heightStamps; } }
        [SerializeField] private SgteTerrainHeightStamps heightStamps;
        public int HeightStampCount {set {if (heightStampCount != value) { heightStampCount = value; RefreshHeightStampsCache(); }} get { return heightStampCount; } }
        [SerializeField] private int heightStampCount = 8;
        private SgtTerrain cachedTerrain;
        private NativeArray<byte> cachedHeightStampData = new NativeArray<byte>();
        private NativeArray<float> tempWeights;
        private NativeArray<int> gridMappings = new NativeArray<int>();
        private NativeArray<bool3> gridFlipAndTranspose = new NativeArray<bool3>();
        private NativeArray<double> localHeightOrigins = new NativeArray<double>();
        private NativeArray<double> localDisplacements = new NativeArray<double>();
        private Unity.Mathematics.Random rand;
        public void MarkAsDirty(bool initRand=true) {

            if (initRand)
                rand = Unity.Mathematics.Random.CreateFromIndex(Seed);

            if (HeightStampCount > 0) {
                try { gridMappings.Dispose(); } catch { };
                try { gridFlipAndTranspose.Dispose(); } catch { }
                int totalGrids = GridsPerDim * GridsPerDim * 6;
                gridMappings = new NativeArray<int>(totalGrids, Allocator.Persistent);
                gridFlipAndTranspose = new NativeArray<bool3>(totalGrids, Allocator.Persistent);
                for (int i = 0; i < gridMappings.Length; i++) {
                    gridMappings[i] = rand.NextInt(0, HeightStampCount);
                    gridFlipAndTranspose[i] = rand.NextBool3();
                }
            }
            if (cachedTerrain != null) {
                cachedTerrain.MarkAsDirty();
            }
        }

        public void RefreshHeightStampsCache() {
            if (HeightStamps != null) {
                try { cachedHeightStampData.Dispose(); } catch { }
                try { localHeightOrigins.Dispose(); } catch { }
                try { localDisplacements.Dispose(); } catch { }
                NativeList<byte> heightStampDataList = new NativeList<byte>(Allocator.Persistent);
                localHeightOrigins = new NativeArray<double>(HeightStampCount, Allocator.Persistent);
                localDisplacements = new NativeArray<double>(HeightStampCount, Allocator.Persistent);
                rand = Unity.Mathematics.Random.CreateFromIndex(Seed);

                if (HeightStampCount > HeightStamps.heightStampConfigs.Count) {
                    // using the private heightStampCount since we don't want to trigger the refreshing again
                    heightStampCount = HeightStamps.heightStampConfigs.Count;
                }
                var selectedHeightStamps = HeightStamps.heightStampConfigs.OrderBy(_ => rand.NextInt()).Take(heightStampCount).ToArray();
                for (int i = 0; i < HeightStampCount; i++) {
                    heightStampDataList.AddRange(selectedHeightStamps[i].stampMap.GetRawTextureData<byte>());
                    localHeightOrigins[i] = selectedHeightStamps[i].localHeightOrigin;
                    localDisplacements[i] = selectedHeightStamps[i].localDisplacement;
                }
                cachedHeightStampData = heightStampDataList.AsArray();
                MarkAsDirty(initRand: false);
            } else {
                heightStampCount = 0;
            }
        }

        protected virtual void OnEnable() {
            cachedTerrain = GetComponent<SgtTerrain>();

            cachedTerrain.OnScheduleHeights += HandleScheduleHeights;
            cachedTerrain.OnScheduleCombinedHeights += HandleScheduleHeights;

            tempWeights = new NativeArray<float>(0, Allocator.Persistent);
            RefreshHeightStampsCache();
        }

        protected virtual void OnDisable() {
            cachedTerrain.OnScheduleHeights -= HandleScheduleHeights;
            cachedTerrain.OnScheduleCombinedHeights -= HandleScheduleHeights;

            cachedTerrain.ScheduleDispose(cachedHeightStampData);
            cachedTerrain.ScheduleDispose(tempWeights);
            cachedTerrain.ScheduleDispose(gridMappings);
            cachedTerrain.ScheduleDispose(localDisplacements);
            cachedTerrain.ScheduleDispose(localHeightOrigins);
            cachedTerrain.ScheduleDispose(gridFlipAndTranspose);
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
            if (HeightStampCount <= 0) return;
            var job = new HeightsJob();
            job.Seed = Seed;
            job.Displacement = Displacement;
            job.HeightOrigin = HeightOrigin;
            var sampleTex = heightStamps.heightStampConfigs[0].stampMap;
            job.StampSize = new int2(sampleTex.width, sampleTex.height);
            job.StampStride = SgtCommon.GetStride(sampleTex.format);
            job.StampOffset = SgtCommon.GetOffset(sampleTex.format, 0);
            job.GridsPerDim = GridsPerDim;
            job.GridsPerQuad = GridsPerDim * GridsPerDim;
            job.GridSpan = GridSpan;
            job.GridMapping = gridMappings;
            job.GridFlipAndTranspose = gridFlipAndTranspose;
            job.StampCount = HeightStampCount;
            job.StampBSize = cachedHeightStampData.Length / HeightStampCount;
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
            [ReadOnly] public NativeArray<int> GridMapping;
            [ReadOnly] public NativeArray<bool3> GridFlipAndTranspose;
            [ReadOnly] public NativeArray<double> LocalHeightOrigins;
            [ReadOnly] public NativeArray<double> LocalDisplacements;
            public double Displacement, HeightOrigin;
            public int2 StampSize;
            public int StampStride, StampOffset;
            public int GridsPerDim;
            public float GridSpan;
            public int StampCount;
            public int StampBSize;
            public int GridsPerQuad;

            [ReadOnly] public int Area;
            [ReadOnly] public int2 AreaSize;
            [ReadOnly] public int AreaSplatCount;
            [ReadOnly] public NativeArray<float> AreaWeights;

            public NativeArray<double> Heights;
            [BurstCompile]
            public int GridIdOf(int quadId, int x, int y) {
                return CoordUtils.IntPosToIndex(
                    x >= 0 ? x : GridsPerDim + x,
                    y >= 0 ? y : GridsPerDim + y,
                    GridsPerDim) + (quadId * GridsPerQuad);
            }

            [BurstCompile]
            public double GetSubHeight(double alpha, (int, int, int, double2) gridIdAnduv) {
                int gridId = GridIdOf(gridIdAnduv.Item1, gridIdAnduv.Item2, gridIdAnduv.Item3);
                double2 uv = gridIdAnduv.Item4;
                //gridId = gridId - (gridId / GridMapping.Length) * GridMapping.Length; // Just in case...
                int stampId = GridMapping[gridId];
                var flipAndTranspose = GridFlipAndTranspose[gridId];
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

            // return: quadIdx, localGridY, localGridY, localUV
            [BurstCompile]
            public (int, int, int, double2) LeftNeighbourGridIdAndUV(int quadIdx, int2 localGrid, double2 localUV) {
                var defaultUV = new double2(localUV.x + 1 - GridSpan, localUV.y);
                switch (quadIdx) {
                    case 0:
                        return localGrid.x > 0
                            ? (
                                0, localGrid.x - 1, localGrid.y, defaultUV
                            ) : (
                                3, -1, localGrid.y, defaultUV
                            );
                    case 1:
                        return localGrid.x > 0
                            ? (
                                1, localGrid.x - 1, localGrid.y, defaultUV
                            ) : (
                                0, -1, localGrid.y, defaultUV
                            );
                    case 2:
                        return localGrid.x > 0
                            ? (
                                2, localGrid.x - 1, localGrid.y, defaultUV
                            ) : (
                                1, -1, localGrid.y, defaultUV
                            );
                    case 3:
                        return localGrid.x > 0
                            ? (
                                3, localGrid.x - 1, localGrid.y, defaultUV
                            ) : (
                                2, -1, localGrid.y, defaultUV
                            );
                    case 4:
                        return localGrid.x > 0
                            ? (
                                4, localGrid.x - 1, localGrid.y, defaultUV
                            ) : (
                                0, -1 - localGrid.y, -1,
                                new double2(1 - localUV.y, 1 - GridSpan + localUV.x)
                            );
                    case 5:
                        return localGrid.x > 0
                            ? (
                                5, localGrid.x - 1, localGrid.y, defaultUV
                            ) : (
                                0, localGrid.y, 0,
                                new double2(localUV.y, GridSpan - localUV.x)
                            );
                    default:
                        return (0, 0, 0, double2.zero);
                }
            }

            [BurstCompile]
            public (int, int, int, double2) RightNeighbourGridIdAndUV(int quadIdx, int2 localGrid, double2 localUV) {
                var defaultUV = new double2(localUV.x - 1 + GridSpan, localUV.y);
                switch (quadIdx) {
                    case 0:
                        return localGrid.x < GridsPerDim - 1
                            ? (
                                0, localGrid.x + 1, localGrid.y, defaultUV
                            ) : (
                                1, 0, localGrid.y, defaultUV
                            );
                    case 1:
                        return localGrid.x < GridsPerDim - 1
                            ? (
                                1, localGrid.x + 1, localGrid.y, defaultUV
                            ) : (
                                2, 0, localGrid.y, defaultUV
                            );
                    case 2:
                        return localGrid.x < GridsPerDim - 1
                            ? (
                                2, localGrid.x + 1, localGrid.y, defaultUV
                            ) : (
                                3, 0, localGrid.y, defaultUV
                            );
                    case 3:
                        return localGrid.x < GridsPerDim - 1
                            ? (
                                3, localGrid.x + 1, localGrid.y, defaultUV
                            ) : (
                                0, 0, localGrid.y, defaultUV
                            );
                    case 4:
                        return localGrid.x < GridsPerDim - 1
                            ? (
                                4, localGrid.x + 1, localGrid.y, defaultUV
                            ) : (
                                2, localGrid.y, -1,
                                new double2(localUV.y, 2 - GridSpan - localUV.x)
                            );
                    case 5:
                        return localGrid.x < GridsPerDim - 1
                            ? (
                                5, localGrid.x + 1, localGrid.y, defaultUV
                            ) : (
                                2, -1 - localGrid.y, 0,
                                new double2(1 - localUV.y, localUV.x - 1 + GridSpan)
                            );
                    default:
                        return (0, 0, 0, double2.zero);
                }
            }

            [BurstCompile]
            public (int, int, int, double2) UpNeighbourGridIdAndUV(int quadIdx, int2 localGrid, double2 localUV) {
                var defaultUV = new double2(localUV.x, localUV.y - 1 + GridSpan);
                switch (quadIdx) {
                    case 0:
                        return localGrid.y < GridsPerDim - 1
                            ? (
                                0, localGrid.x, localGrid.y + 1, defaultUV
                            ) : (
                                4, 0, -1 - localGrid.x,
                                new double2(GridSpan - 1 + localUV.y, 1 - localUV.x)
                            );
                    case 1:
                        return localGrid.y < GridsPerDim - 1
                            ? (
                                1, localGrid.x, localGrid.y + 1, defaultUV
                            ) : (
                                4, localGrid.x, 0, defaultUV
                            );
                    case 2:
                        return localGrid.y < GridsPerDim - 1
                            ? (
                                2, localGrid.x, localGrid.y + 1, defaultUV
                            ) : (
                                4, -1, localGrid.x,
                                new double2(2 - localUV.y - GridSpan, localUV.x)
                            );
                    case 3:
                        return localGrid.y < GridsPerDim - 1
                            ? (
                                3, localGrid.x, localGrid.y + 1, defaultUV
                            ) : (
                                4, -1 - localGrid.x, -1,
                                new double2(1 - localUV.x, 2 - GridSpan - localUV.y)
                            );
                    case 4:
                        return localGrid.y < GridsPerDim - 1
                            ? (
                                4, localGrid.x, localGrid.y + 1, defaultUV
                            ) : (
                                3, -1 - localGrid.x, -1,
                                new double2(1 - localUV.x, 2 - GridSpan - localUV.y)
                            );
                    case 5:
                        return localGrid.y < GridsPerDim - 1
                            ? (
                                5, localGrid.x, localGrid.y + 1, defaultUV
                            ) : (
                                1, localGrid.x, 0, defaultUV
                            );
                    default:
                        return (0, 0, 0, double2.zero);
                }
            }

            [BurstCompile]
            public (int, int, int, double2) DownNeighbourGridIdAndUV(int quadIdx, int2 localGrid, double2 localUV) {
                var defaultUV = new double2(localUV.x, localUV.y + 1 - GridSpan);
                switch (quadIdx) {
                    case 0:
                        return localGrid.y > 0
                            ? (
                                0, localGrid.x, localGrid.y - 1, defaultUV
                            ) : (
                                5, 0, localGrid.x,
                                new double2(GridSpan - localUV.y, localUV.x)
                            );
                    case 1:
                        return localGrid.y > 0
                            ? (
                                1, localGrid.x, localGrid.y - 1, defaultUV
                            ) : (
                                5, localGrid.x, -1, defaultUV
                            );
                    case 2:
                        return localGrid.y > 0
                            ? (
                                2, localGrid.x, localGrid.y - 1, defaultUV
                            ) : (
                                5, -1, -1 - localGrid.x,
                                new double2(1 - GridSpan + localUV.y, 1 - localUV.x)
                            );
                    case 3:
                        return localGrid.y > 0
                            ? (
                                3, localGrid.x, localGrid.y - 1, defaultUV
                            ) : (
                                5, -1 - localGrid.x, 0,
                                new double2(1 - localUV.x, GridSpan - localUV.y)
                            );
                    case 4:
                        return localGrid.y > 0
                            ? (
                                4, localGrid.x, localGrid.y - 1, defaultUV
                            ) : (
                                1, localGrid.x, -1, defaultUV
                            );
                    case 5:
                        return localGrid.y > 0
                            ? (
                                5, localGrid.x, localGrid.y - 1, defaultUV
                            ) : (
                                3, -1 - localGrid.x, 0,
                                new double2(1 - localUV.x, GridSpan - localUV.y)
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
                        localUV *= GridsPerDim;
                        int2 localGrid = new int2(math.floor(localUV));

                        localUV = math.frac(localUV) * (1 - GridSpan) + GridSpan * .5f;

                        int gridsPerQuad = GridsPerDim * GridsPerDim;

                        // gridId, localUV
                        double alpha_K = 1 / GridSpan;
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
                                selfAlpha, (quadIdx, localGrid.x, localGrid.y, localUV));

                        if (leftAlpha > 0) {
                            var leftNeighbourGridIdAndUV =
                                LeftNeighbourGridIdAndUV(quadIdx, localGrid, localUV);
                            if (upAlpha > 0) { // up-left corner
                                localHeight += GetSubHeight(
                                    leftAlpha * (1 - upAlpha),
                                    leftNeighbourGridIdAndUV);

                                localHeight += GetSubHeight(
                                    (1 - leftAlpha) * upAlpha,
                                    UpNeighbourGridIdAndUV(quadIdx, localGrid, localUV));

                                localHeight += GetSubHeight(
                                    leftAlpha * upAlpha,
                                    UpNeighbourGridIdAndUV(
                                        leftNeighbourGridIdAndUV.Item1,
                                        new int2(leftNeighbourGridIdAndUV.Item2, leftNeighbourGridIdAndUV.Item3),
                                        leftNeighbourGridIdAndUV.Item4));
                            } else if (downAlpha > 0) { // down-left corner
                                localHeight += GetSubHeight(
                                    leftAlpha * (1 - downAlpha),
                                    leftNeighbourGridIdAndUV);

                                localHeight += GetSubHeight(
                                    (1 - leftAlpha) * downAlpha,
                                    DownNeighbourGridIdAndUV(quadIdx, localGrid, localUV));

                                localHeight += GetSubHeight(
                                    leftAlpha * downAlpha,
                                    DownNeighbourGridIdAndUV(
                                        leftNeighbourGridIdAndUV.Item1,
                                        new int2(leftNeighbourGridIdAndUV.Item2, leftNeighbourGridIdAndUV.Item3),
                                        leftNeighbourGridIdAndUV.Item4));

                            } else {
                                localHeight += GetSubHeight(
                                    leftAlpha, leftNeighbourGridIdAndUV);
                            }
                        } else if (rightAlpha > 0) {
                            var rightNeighbourGridIdAndUV =
                                RightNeighbourGridIdAndUV(quadIdx, localGrid, localUV);
                            if (upAlpha > 0) { // up-left corner
                                localHeight += GetSubHeight(
                                    rightAlpha * (1 - upAlpha),
                                    rightNeighbourGridIdAndUV);

                                localHeight += GetSubHeight(
                                    (1 - rightAlpha) * upAlpha,
                                    UpNeighbourGridIdAndUV(quadIdx, localGrid, localUV));

                                localHeight += GetSubHeight(
                                    rightAlpha * upAlpha,
                                    UpNeighbourGridIdAndUV(
                                        rightNeighbourGridIdAndUV.Item1,
                                        new int2(rightNeighbourGridIdAndUV.Item2, rightNeighbourGridIdAndUV.Item3),
                                        rightNeighbourGridIdAndUV.Item4));

                            } else if (downAlpha > 0) { // down-left corner
                                localHeight += GetSubHeight(
                                    rightAlpha * (1 - downAlpha),
                                    rightNeighbourGridIdAndUV);

                                localHeight += GetSubHeight(
                                    (1 - rightAlpha) * downAlpha,
                                    DownNeighbourGridIdAndUV(quadIdx, localGrid, localUV));

                                localHeight += GetSubHeight(
                                    rightAlpha * downAlpha,
                                    DownNeighbourGridIdAndUV(
                                        rightNeighbourGridIdAndUV.Item1,
                                        new int2(rightNeighbourGridIdAndUV.Item2, rightNeighbourGridIdAndUV.Item3),
                                        rightNeighbourGridIdAndUV.Item4));

                            } else {
                                localHeight += GetSubHeight(
                                    rightAlpha, rightNeighbourGridIdAndUV);
                            }
                        } else if (upAlpha > 0) {
                            localHeight += GetSubHeight(
                                upAlpha,
                                UpNeighbourGridIdAndUV(quadIdx, localGrid, localUV));
                        } else if (downAlpha > 0) {
                            localHeight += GetSubHeight(
                                downAlpha,
                                DownNeighbourGridIdAndUV(quadIdx, localGrid, localUV));
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
    public class SgteTerrainHeightStampGrid_Editor : CwEditor {
        protected override void OnInspector() {
            TARGET tgt; TARGET[] tgts; GetTargets(out tgt, out tgts);

            var markAsDirty = false;
            var prepareTextures = false;

            markAsDirty |= SgtTerrain_Editor.DrawArea(serializedObject.FindProperty("area"), tgt.GetComponent<SgtTerrain>());

            Separator();

            Draw("seed", ref markAsDirty, "The random seed of distributing the stamps");
            Draw("displacement", ref markAsDirty, "The random seed of distributing the stamps");
            Draw("heightOrigin", ref markAsDirty, "The origin point of the displacement with respect to the pixel value");

            BeginError(Any(tgts, t => t.GridsPerDim == 0));
            Draw("gridsPerDim", ref markAsDirty, "Number of the grids per dimension on each cube face");
            EndError();

            BeginError(Any(tgts, t => t.GridSpan < 0 || t.GridSpan > 1));
            Draw("gridSpan", ref markAsDirty, "The span of each grid for the lerp");
            EndError();

            BeginError(Any(tgts, t => t.HeightStamps == null));
            Draw("heightStamps", ref prepareTextures, "The height stamps library used for this drawing stamps from");
            EndError();

            BeginError(Any(tgts, t => t.HeightStampCount <= 0));
            Draw("heightStampCount", ref prepareTextures, "Number of stamps to drawn from the HeightStamps scriptable obejct, a big number decreases the reptitiveness of the planet terrain but consumes more memory");
            EndError();

            // Manual refresh, e.g: when the HeightStamp scriptable object is updated....
            if (Button("Force Refresh Height Stamps")) {
                tgt.RefreshHeightStampsCache();
                DirtyAndUpdate();
            }

            if (prepareTextures == true) {
                Each(tgts, t => t.RefreshHeightStampsCache(), true, true);
            }

            if (markAsDirty == true) {
                Each(tgts, t => t.MarkAsDirty(), true, true);
            }
        }
    }

}
#endif