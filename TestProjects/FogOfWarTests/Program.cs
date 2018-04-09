﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Numerics;
using Canvas3D;
using OpenMOBA;
using OpenMOBA.DataStructures;
using OpenMOBA.DevTool.Debugging;
using OpenMOBA.Foundation;
using OpenMOBA.Foundation.Terrain;
using OpenMOBA.Foundation.Terrain.CompilationResults.Local;
using OpenMOBA.Foundation.Terrain.CompilationResults.Overlay;
using OpenMOBA.Foundation.Terrain.Declarations;
using OpenMOBA.Geometry;

namespace FogOfWarTests {
   public class Program {
      private static readonly float renderScale = 1.0f;
      private static readonly Size bounds = new Size(1280, 720);
      private static readonly Random random = new Random(3);
      private static readonly DebugMultiCanvasHost host = DebugMultiCanvasHost.CreateAndShowCanvas(bounds, new Point(0, 0), CreateCanvasProjector());
      private static int frameCounter = 0;

      public static void Main(string[] args) {
         var sectorGraphDescriptionStore = new SectorGraphDescriptionStore();
         var snapshotCompiler = new TerrainSnapshotCompiler(sectorGraphDescriptionStore);
         var terrainService = new TerrainService(sectorGraphDescriptionStore, snapshotCompiler);

         // Add test sectors
         var leftSnd = terrainService.CreateSectorNodeDescription(SectorMetadataPresets.HashCircle2);
         leftSnd.EnableDebugHighlight = true;
         leftSnd.WorldTransform = Matrix4x4.CreateTranslation(-1500, -500, 0);
         terrainService.AddSectorNodeDescription(leftSnd);

         var centerSnd = terrainService.CreateSectorNodeDescription(SectorMetadataPresets.Blank2D);
         centerSnd.WorldTransform = Matrix4x4.CreateTranslation(-500, -500, 0);
         terrainService.AddSectorNodeDescription(centerSnd);

         var rightSnd = terrainService.CreateSectorNodeDescription(SectorMetadataPresets.HashCircle2);
         rightSnd.WorldTransform = Matrix4x4.CreateTranslation(500, -500, 0);
         terrainService.AddSectorNodeDescription(rightSnd);

         // edges between test sectors
         terrainService.AddSectorEdgeDescription(PortalSectorEdgeDescription.Build(
            leftSnd, centerSnd,
            new IntLineSegment2(new IntVector2(1000, 600), new IntVector2(1000, 800)),
            new IntLineSegment2(new IntVector2(0, 600), new IntVector2(0, 800))));
         terrainService.AddSectorEdgeDescription(PortalSectorEdgeDescription.Build(
            centerSnd, leftSnd,
            new IntLineSegment2(new IntVector2(0, 600), new IntVector2(0, 800)),
            new IntLineSegment2(new IntVector2(1000, 600), new IntVector2(1000, 800))));

         //
         terrainService.AddSectorEdgeDescription(PortalSectorEdgeDescription.Build(
            centerSnd, rightSnd,
            new IntLineSegment2(new IntVector2(1000, 600), new IntVector2(1000, 800)),
            new IntLineSegment2(new IntVector2(0, 600), new IntVector2(0, 800))));
         terrainService.AddSectorEdgeDescription(PortalSectorEdgeDescription.Build(
            rightSnd, centerSnd,
            new IntLineSegment2(new IntVector2(0, 600), new IntVector2(0, 800)),
            new IntLineSegment2(new IntVector2(1000, 600), new IntVector2(1000, 800))));

         terrainService.AddSectorEdgeDescription(PortalSectorEdgeDescription.Build(
            centerSnd, rightSnd,
            new IntLineSegment2(new IntVector2(1000, 200), new IntVector2(1000, 400)),
            new IntLineSegment2(new IntVector2(0, 200), new IntVector2(0, 400))));
         terrainService.AddSectorEdgeDescription(PortalSectorEdgeDescription.Build(
            rightSnd, centerSnd,
            new IntLineSegment2(new IntVector2(0, 200), new IntVector2(0, 400)),
            new IntLineSegment2(new IntVector2(1000, 200), new IntVector2(1000, 400))));

         // add some obstacles
         terrainService.AddTemporaryHoleDescription(terrainService.CreateHoleDescription(
            HoleStaticMetadata.CreateRectangleHoleMetadata(-500, 250, 60, 60, 0)));
         terrainService.AddTemporaryHoleDescription(terrainService.CreateHoleDescription(
            HoleStaticMetadata.CreateRectangleHoleMetadata(0, 130, 60, 60, 0)));

         var terrainSnapshot = terrainService.CompileSnapshot();
         var overlayNetwork = terrainSnapshot.OverlayNetworkManager.CompileTerrainOverlayNetwork(15);
         var canvas = host.CreateAndAddCanvas(0);
         foreach (var terrainNode in overlayNetwork.TerrainNodes) {
            canvas.Transform = terrainNode.SectorNodeDescription.WorldTransform;
            canvas.DrawPolyNode(terrainNode.LandPolyNode, StrokeStyle.BlackHairLineSolid, StrokeStyle.DarkRedHairLineSolid);
         }

         foreach (var terrainNode in overlayNetwork.TerrainNodes) {
            canvas.Transform = terrainNode.SectorNodeDescription.WorldTransform;
            foreach (var outboundEdgeGroup in terrainNode.OutboundEdgeGroups) {
               foreach (var outboundEdge in outboundEdgeGroup.Value) {
                  Console.WriteLine(outboundEdge.EdgeJob.SourceSegment);
                  canvas.DrawLine(outboundEdge.EdgeJob.SourceSegment, StrokeStyle.CyanThick3Solid);
               }
            }
         }

         void Y(TerrainOverlayNetworkNode terrainNode) {

            canvas.Transform = terrainNode.SectorNodeDescription.WorldTransform;

            var visibilityPolygonOrigin = new IntVector2(800, 700);
            canvas.DrawPoint(visibilityPolygonOrigin, StrokeStyle.RedThick25Solid);
            var visibilityPolygon = VisibilityPolygon.Create(visibilityPolygonOrigin.ToDoubleVector2(), terrainNode.LandPolyNode.FindContourAndChildHoleBarriers());
            canvas.DrawVisibilityPolygon(visibilityPolygon, fillStyle: new FillStyle(Color.FromArgb(120, 255, 0, 0)));

            var visibleCrossoverSegmentsByNeighbor = MultiValueDictionary<TerrainOverlayNetworkNode, IntLineSegment2>.Create(() => new HashSet<IntLineSegment2>());
            foreach (var outboundEdgeGroup in terrainNode.OutboundEdgeGroups) {
               var otherTerrainNode = outboundEdgeGroup.Key;
               foreach (var outboundEdge in outboundEdgeGroup.Value) {
                  var ranges = visibilityPolygon.Get();
                  var localCrossoverSegment = outboundEdge.EdgeJob.EdgeDescription.SourceSegment;
                  var remoteCrossoverSegment = outboundEdge.EdgeJob.EdgeDescription.DestinationSegment;
                  var rangeIndexIntervals = visibilityPolygon.RangeStab(localCrossoverSegment);
                  foreach (var (startIndexInclusive, endIndexInclusive) in rangeIndexIntervals) {
                     for (var i = startIndexInclusive; i <= endIndexInclusive; i++) {
                        var seg = ranges[i].Segment;

                        var rstart = DoubleVector2.FromRadiusAngle(100, ranges[i].ThetaStart);
                        var rend = DoubleVector2.FromRadiusAngle(100, ranges[i].ThetaEnd);

//                        DoubleVector2 visibleStart, visibleEnd;
                        double visibleStartT, visibleEndT;
                        if (!GeometryOperations.TryFindNonoverlappingLineLineIntersectionT(localCrossoverSegment.First.ToDoubleVector2(), localCrossoverSegment.Second.ToDoubleVector2(), visibilityPolygonOrigin.ToDoubleVector2(), visibilityPolygonOrigin.ToDoubleVector2() + rstart, out visibleStartT) ||
                            !GeometryOperations.TryFindNonoverlappingLineLineIntersectionT(localCrossoverSegment.First.ToDoubleVector2(), localCrossoverSegment.Second.ToDoubleVector2(), visibilityPolygonOrigin.ToDoubleVector2(), visibilityPolygonOrigin.ToDoubleVector2() + rend, out visibleEndT)) {
                           // wtf?
                           Console.WriteLine("???");
                           continue;
                        }

                        if (visibilityPolygon.SegmentComparer.Compare(localCrossoverSegment, seg) < 0) {
                           canvas.DrawLine(seg, StrokeStyle.LimeThick5Solid);
                           var localVisibleStart = localCrossoverSegment.PointAt(visibleStartT);
                           var localVisibleEnd = localCrossoverSegment.PointAt(visibleEndT);

                           canvas.FillTriangle(
                              visibilityPolygonOrigin.ToDoubleVector2(),
                              localVisibleStart,
                              localVisibleEnd,
                              new FillStyle(Color.FromArgb(120, 0, 255, 255)));

                           var remoteVisibleStart = remoteCrossoverSegment.PointAt(visibleStartT);
                           var remoteVisibleEnd = remoteCrossoverSegment.PointAt(visibleEndT);
                           visibleCrossoverSegmentsByNeighbor.Add(otherTerrainNode, new IntLineSegment2(remoteVisibleStart.LossyToIntVector2(), remoteVisibleEnd.LossyToIntVector2()));
                        } else {
                           canvas.DrawLine(seg, StrokeStyle.RedThick5Solid);
                        }
                     }
                  }
               }
            }

            var visibilityPolygonOriginWorld = Vector3.Transform(new Vector3(visibilityPolygonOrigin.ToDotNetVector(), 0), terrainNode.SectorNodeDescription.WorldTransform);
            foreach (var (neighbor, inboundCrossoverSegments) in visibleCrossoverSegmentsByNeighbor) {
               var neighborPolygonOrigin = Vector3.Transform(visibilityPolygonOriginWorld, neighbor.SectorNodeDescription.WorldTransformInv);
               //visibilityPolygonOrigin
               Z(new IntVector2((int)neighborPolygonOrigin.X, (int)neighborPolygonOrigin.Y), neighbor, inboundCrossoverSegments, new HashSet<TerrainOverlayNetworkNode>{terrainNode});
            }
         }

         void Z(IntVector2 visibilityPolygonOrigin, TerrainOverlayNetworkNode terrainNode, IReadOnlyCollection<IntLineSegment2> inboundCrossoverSegments, HashSet<TerrainOverlayNetworkNode> visited) {
            var zzz = terrainNode.LandPolyNode.Contour;
            var zzy = terrainNode.LandPolyNode.Childs.FirstOrDefault()?.Contour;
            if (zzy != null) {
               var clkzzz = GeometryOperations.Clockness(zzz[0], zzz[1], zzz[2]);
               var clkzzy = GeometryOperations.Clockness(zzy[0], zzy[1], zzy[2]);
                              Console.WriteLine("!");
            }

            canvas.Transform = terrainNode.SectorNodeDescription.WorldTransform;

            canvas.DrawPoint(visibilityPolygonOrigin, StrokeStyle.RedThick25Solid);
            var visibilityPolygon = new VisibilityPolygon(
               visibilityPolygonOrigin.ToDoubleVector2(),
               new[] {
                  new VisibilityPolygon.IntervalRange {
                     Id = VisibilityPolygon.RANGE_ID_INFINITESIMALLY_NEAR,
                     ThetaStart = 0,
                     ThetaEnd = VisibilityPolygon.TwoPi
                  }, 
               });
            
            foreach (var inboundCrossoverSegment in inboundCrossoverSegments) {
               visibilityPolygon.ClearBefore(inboundCrossoverSegment);
            }

            Console.WriteLine("====");

            foreach (var seg in terrainNode.LandPolyNode.FindContourAndChildHoleBarriers()) {
               if (GeometryOperations.Clockness(visibilityPolygon.Origin, seg.First.ToDoubleVector2(), seg.Second.ToDoubleVector2()) == Clockness.CounterClockwise) {
                  continue;
               }
               visibilityPolygon.Insert(seg);
               Console.WriteLine(seg);
            }
               Console.WriteLine("====");

            canvas.DrawVisibilityPolygon(visibilityPolygon, fillStyle: new FillStyle(Color.FromArgb(120, 0, 0, 255)));

            ///
            /// 
            var visibleCrossoverSegmentsByNeighbor = MultiValueDictionary<TerrainOverlayNetworkNode, IntLineSegment2>.Create(() => new HashSet<IntLineSegment2>());
            foreach (var outboundEdgeGroup in terrainNode.OutboundEdgeGroups) {
               var otherTerrainNode = outboundEdgeGroup.Key;
               if (visited.Contains(otherTerrainNode)) continue;

               foreach (var outboundEdge in outboundEdgeGroup.Value) {
                  var ranges = visibilityPolygon.Get();
                  var localCrossoverSegment = outboundEdge.EdgeJob.EdgeDescription.SourceSegment;
                  var remoteCrossoverSegment = outboundEdge.EdgeJob.EdgeDescription.DestinationSegment;
                  var rangeIndexIntervals = visibilityPolygon.RangeStab(localCrossoverSegment);
                  foreach (var (startIndexInclusive, endIndexInclusive) in rangeIndexIntervals) {
                     for (var i = startIndexInclusive; i <= endIndexInclusive; i++) {
                        if (ranges[i].Id == VisibilityPolygon.RANGE_ID_INFINITELY_FAR || ranges[i].Id == VisibilityPolygon.RANGE_ID_INFINITESIMALLY_NEAR) continue;
                        var seg = ranges[i].Segment;

                        var rstart = DoubleVector2.FromRadiusAngle(100, ranges[i].ThetaStart);
                        var rend = DoubleVector2.FromRadiusAngle(100, ranges[i].ThetaEnd);

                        //                        DoubleVector2 visibleStart, visibleEnd;
                        double visibleStartT, visibleEndT;
                        if (!GeometryOperations.TryFindNonoverlappingLineLineIntersectionT(localCrossoverSegment.First.ToDoubleVector2(), localCrossoverSegment.Second.ToDoubleVector2(), visibilityPolygonOrigin.ToDoubleVector2(), visibilityPolygonOrigin.ToDoubleVector2() + rstart, out visibleStartT) ||
                            !GeometryOperations.TryFindNonoverlappingLineLineIntersectionT(localCrossoverSegment.First.ToDoubleVector2(), localCrossoverSegment.Second.ToDoubleVector2(), visibilityPolygonOrigin.ToDoubleVector2(), visibilityPolygonOrigin.ToDoubleVector2() + rend, out visibleEndT)) {
                           // wtf?
                           Console.WriteLine("???");
                           continue;
                        }

                        if (visibilityPolygon.SegmentComparer.Compare(localCrossoverSegment, seg) < 0) {
                           canvas.DrawLine(seg, StrokeStyle.LimeThick5Solid);
                           var localVisibleStart = localCrossoverSegment.PointAt(visibleStartT);
                           var localVisibleEnd = localCrossoverSegment.PointAt(visibleEndT);

                           canvas.FillTriangle(
                              visibilityPolygonOrigin.ToDoubleVector2(),
                              localVisibleStart,
                              localVisibleEnd,
                              new FillStyle(Color.FromArgb(120, 0, 255, 255)));

                           var remoteVisibleStart = remoteCrossoverSegment.PointAt(visibleStartT);
                           var remoteVisibleEnd = remoteCrossoverSegment.PointAt(visibleEndT);
                           visibleCrossoverSegmentsByNeighbor.Add(otherTerrainNode, new IntLineSegment2(remoteVisibleStart.LossyToIntVector2(), remoteVisibleEnd.LossyToIntVector2()));
                        } else {
                           canvas.DrawLine(seg, StrokeStyle.RedThick5Solid);
                        }
                     }
                  }
               }
            }

            var visibilityPolygonOriginWorld = Vector3.Transform(new Vector3(visibilityPolygonOrigin.ToDotNetVector(), 0), terrainNode.SectorNodeDescription.WorldTransform);
            foreach (var (neighbor, nextInboundCrossoverSegments) in visibleCrossoverSegmentsByNeighbor) {
               var neighborPolygonOrigin = Vector3.Transform(visibilityPolygonOriginWorld, neighbor.SectorNodeDescription.WorldTransformInv);
               //visibilityPolygonOrigin
               Z(new IntVector2((int)neighborPolygonOrigin.X, (int)neighborPolygonOrigin.Y), neighbor, nextInboundCrossoverSegments,
                  visited.Concat(new[]{terrainNode}).ToHashSet());
            }
         }

         int asdfa = -1;
         foreach (var terrainNode in overlayNetwork.TerrainNodes) {
            asdfa++;
            if (asdfa != 0 && asdfa != 2) continue;
//            if (asdfa != 2) continue;

            canvas.Transform = terrainNode.SectorNodeDescription.WorldTransform;
            canvas.DrawRectangle(new IntRect2(0, 0, 30, 30), 0, StrokeStyle.RedHairLineSolid);

            if (terrainNode.SectorNodeDescription.EnableDebugHighlight) {
               Y(terrainNode);
            }
         }
      }

      private static PerspectiveProjector CreateCanvasProjector() {
         var rotation = 95 * Math.PI / 180.0;
         var displaySize = new Size((int)(bounds.Width * renderScale), (int)(bounds.Height * renderScale));
         var center = new DoubleVector3(0, 0, 0);
         var projector = new PerspectiveProjector(
            center + DoubleVector3.FromRadiusAngleAroundXAxis(1000, rotation),
            center,
            DoubleVector3.FromRadiusAngleAroundXAxis(1, rotation - Math.PI / 2),
            displaySize.Width,
            displaySize.Height);
         return projector;
      }
   }
}