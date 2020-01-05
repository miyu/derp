﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dargon.Dviz;

namespace Dargon.Dviz {
   public static class DebugCanvas3DExtensions {
      public static void DrawPoints(this IDebugCanvas canvas, IReadOnlyList<Vector3> points, StrokeStyle strokeStyle) {
         canvas.BatchDraw(() => {
            foreach (var point in points) {
               canvas.DrawPoint(point, strokeStyle);
            }
         });
      }

      public static void DrawLineList(this IDebugCanvas canvas, IReadOnlyList<Vector3> points, StrokeStyle strokeStyle) {
         if (points.Count % 2 != 0) {
            throw new ArgumentException("Line List points must have even length.");
         }

         canvas.BatchDraw(() => {
            for (var i = 0; i < points.Count; i += 2) {
               var a = points[i];
               var b = points[i + 1];
               canvas.DrawLine(a, b, strokeStyle);
            }
         });
      }

      public static void DrawLineStrip(this IDebugCanvas canvas, IReadOnlyList<Vector3> points, StrokeStyle strokeStyle) {
         canvas.BatchDraw(() => {
            for (var i = 0; i < points.Count - 1; i++) {
               var a = points[i];
               var b = points[i + 1];
               canvas.DrawLine(a, b, strokeStyle);
            }
         });
      }
   }
}