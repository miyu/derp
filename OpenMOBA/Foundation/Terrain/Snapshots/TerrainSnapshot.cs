﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ClipperLib;
using OpenMOBA.Foundation.Visibility;
using OpenMOBA.Geometry;

namespace OpenMOBA.Foundation.Terrain.Snapshots {
   public class TerrainSnapshot {
      public int Version { get; set; }
      public IReadOnlyList<CrossoverSnapshot> Crossovers { get; set; }
      public IReadOnlyList<SectorSnapshot> SectorSnapshots { get; set; }
      public IReadOnlyList<DynamicTerrainHole> TemporaryHoles { get; set; }
   }

   public class CrossoverSnapshot {
      public Crossover Crossover { get; set; }
      public SectorSnapshot Remote { get; set; }
      public IntLineSegment2 LocalSegment { get; set; }
      public IntLineSegment2 RemoteSegment { get; set; }
   }

   public class SectorSnapshot {
      private const int kCrossoverAdditionalPathingDilation = 2;

      // Inputs
      public Sector Sector { get; set; }
      public TerrainStaticMetadata StaticMetadata => Sector.StaticMetadata;
      public Matrix4x4 WorldTransform;
      public Matrix4x4 WorldTransformInv;

      public List<DynamicTerrainHole> TemporaryHoles { get; set; } = new List<DynamicTerrainHole>();
      public List<CrossoverSnapshot> CrossoverSnapshots { get; set; } = new List<CrossoverSnapshot>();
      public Dictionary<Tuple<CrossoverSnapshot, CrossoverSnapshot>, List<IntLineSegment2>> BarriersBetweenCrossovers { get; set; } = new Dictionary<Tuple<CrossoverSnapshot, CrossoverSnapshot>, List<IntLineSegment2>>();

      // Outputs
      private readonly Dictionary<double, PolyTree> dilatedHolesUnionCache = new Dictionary<double, PolyTree>();
      private readonly Dictionary<double, IntLineSegment2?[]> erodedCrossoverSegmentsCache = new Dictionary<double, IntLineSegment2?[]>();
      private readonly Dictionary<double, Dictionary<DoubleVector2, VisibilityPolygon>> lineOfSightCaches = new Dictionary<double, Dictionary<DoubleVector2, VisibilityPolygon>>();
      private readonly Dictionary<double, PolyTree> punchedLandCache = new Dictionary<double, PolyTree>();
      private readonly Dictionary<double, Triangulation> triangulationCache = new Dictionary<double, Triangulation>();
      private readonly Dictionary<double, SectorVisibilityGraph> visibilityGraphCache = new Dictionary<double, SectorVisibilityGraph>();

      public DoubleVector2 WorldToLocal(IntVector3 p) => WorldToLocal(p.ToDoubleVector3());
      public DoubleVector2 WorldToLocal(DoubleVector3 p) => Vector3.Transform(p.ToDotNetVector(), WorldTransformInv).ToOpenMobaVector().XY;
      public IntLineSegment2 WorldToLocal(IntLineSegment3 s) => new IntLineSegment2(WorldToLocal(s.First).LossyToIntVector2(), WorldToLocal(s.Second).LossyToIntVector2());

      public PolyTree ComputeDilatedHolesUnion(double holeDilationRadius) {
         PolyTree dilatedHolesUnion;
         if (!dilatedHolesUnionCache.TryGetValue(holeDilationRadius, out dilatedHolesUnion)) {
            dilatedHolesUnion = PolygonOperations.Offset()
                                                 .Include(StaticMetadata.LocalExcludedContours)
                                                 .Include(TemporaryHoles.SelectMany(h => h.Polygons))
                                                 .Dilate(holeDilationRadius)
                                                 .Execute();
            dilatedHolesUnionCache[holeDilationRadius] = dilatedHolesUnion;
         }
         return dilatedHolesUnion;
      }

      public IntLineSegment2?[] ComputeErodedCrossoverSegments(double segmentDilationRadius) {
         IntLineSegment2?[] result;
         if (erodedCrossoverSegmentsCache.TryGetValue(segmentDilationRadius, out result)) {
            return result;
         }

         result = new IntLineSegment2?[CrossoverSnapshots.Count];
         for (var i = 0; i < result.Length; i++) {
            var segment = CrossoverSnapshots[i].LocalSegment;
            var a = segment.First.ToDoubleVector2();
            var b = segment.Second.ToDoubleVector2();
            var aToB = a.To(b);
            var aToBMag = aToB.Norm2D();
            if (aToBMag <= 2 * segmentDilationRadius) continue;

            var shrink = (aToB * segmentDilationRadius / aToBMag).LossyToIntVector2();
            result[i] = new IntLineSegment2(
               a.LossyToIntVector2() + shrink,
               b.LossyToIntVector2() - shrink);
         }
         return erodedCrossoverSegmentsCache[segmentDilationRadius] = result;
      }

      public PolyTree ComputePunchedLand(double holeDilationRadius) {
         PolyTree punchedLand;
         if (punchedLandCache.TryGetValue(holeDilationRadius, out punchedLand)) {
            return punchedLand;
         }

         var landPoly = PolygonOperations.Offset()
                                         .Include(StaticMetadata.LocalIncludedContours)
                                         .Erode(holeDilationRadius)
                                         .Execute().FlattenToPolygons();

         var crossoverErosionFactor = (int)Math.Ceiling(holeDilationRadius * 2);
         var crossoverDilationFactor = crossoverErosionFactor / 2 + 2;
         var erodedCrossovers = ComputeErodedCrossoverSegments(crossoverErosionFactor);
         var crossoverLandPolys = erodedCrossovers.Where(s => s.HasValue)
                                                  .SelectMany(s => PolylineOperations.ExtrudePolygon(s.Value.Points, crossoverDilationFactor)
                                                                                     .FlattenToPolygons());

         var dilatedHolesUnion = ComputeDilatedHolesUnion(holeDilationRadius);
         punchedLand = PolygonOperations.Punch()
                                          .Include(landPoly)
                                          .Include(crossoverLandPolys)
                                          .Exclude(dilatedHolesUnion.FlattenToPolygons())
                                          .Execute();

         PrunePolytree(punchedLand);
         PopulatePolytreeCrossoverLabels(punchedLand, holeDilationRadius);
         punchedLandCache[holeDilationRadius] = punchedLand;
         return punchedLand;
      }

      private void PopulatePolytreeCrossoverLabels(PolyTree punchedLand, double holeDilationRadius) {
         var crossoverToPolyNode = punchedLand.visibilityGraphTreeData.CrossoverPolyNodes = new Dictionary<Crossover, PolyNode>();
         var crossoverSnapshotToPolyNode = punchedLand.visibilityGraphTreeData.CrossoverSnapshotPolyNodes = new Dictionary<CrossoverSnapshot, PolyNode>();
         var erodedCrossoverSegments = ComputeErodedCrossoverSegments(holeDilationRadius + kCrossoverAdditionalPathingDilation);
         for (var i = 0; i < erodedCrossoverSegments.Length; i++) {
            var segmentBox = erodedCrossoverSegments[i];
            if (!segmentBox.HasValue) {
               continue;
            }

            var segment = segmentBox.Value;

            PolyNode match;
            bool isHole;
            punchedLand.PickDeepestPolynode(segment.First, out match, out isHole);

            if (isHole) {
               throw new InvalidOperationException("Crossover in hole");
            }

            if (match.visibilityGraphNodeData.CrossoverSnapshots == null) {
               match.visibilityGraphNodeData.CrossoverSnapshots = new List<CrossoverSnapshot>();
            }

            if (match.visibilityGraphNodeData.ErodedCrossoverSegments == null) {
               match.visibilityGraphNodeData.ErodedCrossoverSegments = new List<IntLineSegment2>();
            }

            match.visibilityGraphNodeData.CrossoverSnapshots.Add(CrossoverSnapshots[i]);
            match.visibilityGraphNodeData.ErodedCrossoverSegments.Add(segment);
            crossoverToPolyNode.Add(CrossoverSnapshots[i].Crossover, match);
            crossoverSnapshotToPolyNode.Add(CrossoverSnapshots[i], match);
         }
      }

      private void PrunePolytree(PolyNode polyTree) {
         for (var i = polyTree.Childs.Count - 1; i >= 0; i--) {
            var child = polyTree.Childs[i];
            if (Math.Abs(Clipper.Area(child.Contour)) < 16 * 16) {
               Console.WriteLine("Prune: " + Clipper.Area(child.Contour) + " " + child.Contour.Count);
               polyTree.Childs.RemoveAt(i);
               continue;
            }

            PrunePolytree(child);
         }
      }

      public void ComputeCrossoverVisibilities(double holeDilationRadius) {
//         var visibilityGraph = ComputeVisibilityGraph(holeDilationRadius);
//
//         var crossovers = Crossovers.Values.SelectMany(cs => cs).ToList();
//         var locations = crossovers.Select(c => new IntLineSegment2(WorldToLocal(c.Segment.First).LossyToIntVector2(), WorldToLocal(c.Segment.Second).LossyToIntVector2())).ToList();
//         for (var i = 0; i < crossovers.Count; i++) {
//            var ca = crossovers[i];
//            var a = locations[i];
//            for (var j = i + 1; j < crossovers.Count; j++) {
//               var cb = crossovers[j];
//               var b = locations[j];
//
//               var hull = GeometryOperations.ConvexHull(new[] { a.First, a.Second, b.First, b.Second });
//               foreach (var holeContour in StaticMetadata.LocalExcludedContours) {
//                  Trace.Assert(holeContour.IsClosed);
//
//                  var contourPoints = holeContour.Points;
//                  var interiorClockness = Clockness.Neither;
//                  for (i = 0; i < contourPoints.Count - 1; i++) {
////                     var clockness = GeometryOperations.Clockness(contourPoints[i]
//                  }
//               }
//            }
//         }
      }

//      public VisibilityPolygonBuilder ComputeVisibilityPolygon(DoubleVector2 position, double holeDilationRadius) {
//         Dictionary<DoubleVector2, VisibilityPolygonBuilder> lineOfSightCache;
//         if (!lineOfSightCaches.TryGetValue(holeDilationRadius, out lineOfSightCache)) {
//            lineOfSightCache = new Dictionary<DoubleVector2, VisibilityPolygonBuilder>();
//            lineOfSightCaches[holeDilationRadius] = lineOfSightCache;
//         }
//
//         VisibilityPolygonBuilder lineOfSight;
//         if (!lineOfSightCache.TryGetValue(position, out lineOfSight)) {
//            var barriers = ComputeVisibilityGraph(holeDilationRadius).Barriers;
//            lineOfSight = new VisibilityPolygonBuilder(position);
//            foreach (var barrier in barriers) lineOfSight.Insert(barrier);
//            lineOfSightCache[position] = lineOfSight;
//         }
//
//         return lineOfSight;
//      }

      public Triangulation ComputeTriangulation(double holeDilationRadius) {
         Triangulation triangulation;
         if (!triangulationCache.TryGetValue(holeDilationRadius, out triangulation)) {
            var triangulator = new Triangulator();
            triangulation = triangulator.Triangulate(ComputePunchedLand(holeDilationRadius));
            triangulationCache[holeDilationRadius] = triangulation;
         }
         return triangulation;
      }
   }
}
