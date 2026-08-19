using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace AICon
{
    // Real face-to-face wall dimensioning — the thing the existing create_dimension tool explicitly
    // can't do (its own description says it only has element midpoints, not actual wall faces).
    //
    // Uses ReferenceIntersector (which needs a View3D — created here as a throwaway and deleted again)
    // to shoot a ray perpendicular to wallA, starting from a point ON wallA's centerline. Because that
    // start point sits INSIDE wallA's own solid, the nearest hit along the ray is naturally wallA's own
    // face on the side facing wallB, and the next hit is wallB's near face — exactly the "clear gap"
    // dimension an architect would draw between two walls. No AI needed for this, ordinary geometry.
    //
    // The AI decision layer is consulted ONLY when the ray finds MORE than 2 candidate faces (a third
    // wall/return in the way — genuinely ambiguous which pair is "the" dimension) or the two walls
    // aren't roughly parallel. If that call is unavailable or low-confidence, this falls back to the
    // two closest faces and says so in the result — it never blocks or fails just because the AI step
    // didn't answer.
    internal static partial class ToolDispatcher
    {
        private const double WallDimParallelToleranceDegrees = 5.0;

        internal static object CreateWallDimension(Document doc, Dictionary<string, object> args)
        {
            int? aId = Json.GetInt(args, "wall_a_id");
            int? bId = Json.GetInt(args, "wall_b_id");
            if (!aId.HasValue || !bId.HasValue)
                throw new InvalidOperationException("'wall_a_id' and 'wall_b_id' are required.");

            Wall wallA = doc.GetElement(ElementIdCompat.FromInt(aId.Value)) as Wall;
            Wall wallB = doc.GetElement(ElementIdCompat.FromInt(bId.Value)) as Wall;
            if (wallA == null || wallB == null)
                throw new InvalidOperationException("'wall_a_id'/'wall_b_id' must both be existing walls.");

            View view = ResolveView(doc, args);
            if (view is View3D)
                throw new InvalidOperationException("Dimensions can't be created in a 3D view — pass a plan/section/elevation view_id (default: active view, if it isn't 3D).");

            Line lineA = (wallA.Location as LocationCurve)?.Curve as Line;
            Line lineB = (wallB.Location as LocationCurve)?.Curve as Line;
            if (lineA == null || lineB == null)
                throw new InvalidOperationException("Both walls must be straight — curved walls aren't supported by this tool.");

            XYZ dirA = lineA.Direction;
            XYZ dirB = lineB.Direction;
            double angleDeg = dirA.AngleTo(dirB) * 180.0 / Math.PI;
            bool parallel = angleDeg < WallDimParallelToleranceDegrees || Math.Abs(angleDeg - 180.0) < WallDimParallelToleranceDegrees;

            XYZ dimPoint = args.ContainsKey("probe_point_mm")
                ? PointFromArg(args, "probe_point_mm", 2) + new XYZ(0, 0, lineA.Evaluate(0.5, true).Z)
                : lineA.Evaluate(0.5, true);

            double offsetMmArg = Json.GetDouble(args, "offset_mm") ?? 0.0;
            string note;
            // Single, user-initiated call: the AI tie-breaker is worth its latency here.
            Dimension made = DimensionWallPair(doc, view, wallA, wallB, lineA, lineB, dimPoint, offsetMmArg, true, out note);
            if (made == null) throw new InvalidOperationException(note);

            var singleResult = new Dictionary<string, object>
            {
                { "id", made.Id.ToInt() },
                { "value_mm", made.Value.HasValue ? (object)Math.Round(FtToMm(made.Value.Value), 1) : null },
                { "view", view.Name },
                { "wall_a", wallA.Name },
                { "wall_b", wallB.Name }
            };
            if (note != null) singleResult["note"] = note;
            return singleResult;
        }

        /// <summary>
        /// The shared face-to-face core. 'allowAiEscalation' is deliberately OFF for AR400's bulk
        /// dimensioning: each AI tie-break costs ~10s against a local model, which would turn one
        /// sheet into several minutes, and the nearest-face pair is the right answer in almost every
        /// real case anyway. Returns null (with a reason in 'note') rather than throwing.
        /// </summary>
        internal static Dimension DimensionWallPair(Document doc, View view, Wall wallA, Wall wallB,
            Line lineA, Line lineB, XYZ dimPoint, double offsetMm, bool allowAiEscalation, out string note,
            View3D scratchView = null)
        {
            note = null;
            XYZ dirA = lineA.Direction;
            double angleDeg = dirA.AngleTo(lineB.Direction) * 180.0 / Math.PI;
            bool parallel = angleDeg < WallDimParallelToleranceDegrees ||
                            Math.Abs(angleDeg - 180.0) < WallDimParallelToleranceDegrees;

            XYZ perpA = new XYZ(-dirA.Y, dirA.X, 0).Normalize();
            XYZ towardB = lineB.Evaluate(0.5, true) - dimPoint;
            XYZ rayDir = perpA.DotProduct(towardB) < 0 ? -perpA : perpA;

            // ReferenceIntersector needs a 3D view. Creating and deleting one PER PAIR is very
            // expensive (AR400 can ask for dozens per plan), so a caller doing many pairs passes one
            // scratch view in and disposes of it once; only the single-shot tool path makes its own.
            View3D tempView = scratchView;
            bool ownsScratch = tempView == null;
            if (ownsScratch)
            {
                ViewFamilyType vft3d = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                if (vft3d == null) { note = "This project has no 3D view family type (needed internally to find wall faces)."; return null; }
                tempView = View3D.CreateIsometric(doc, vft3d.Id);
            }

            try
            {
                var intersector = new ReferenceIntersector(
                    new List<ElementId> { wallA.Id, wallB.Id }, FindReferenceTarget.Face, tempView);
                List<ReferenceWithContext> hits = intersector.Find(dimPoint, rayDir)
                    .Where(h => h.GetReference() != null)
                    .OrderBy(h => h.Proximity)
                    .ToList();

                if (hits.Count < 2)
                {
                    note = "Could not find 2 wall faces along the ray between these walls — they may not " +
                           "face each other at this point. Try a different probe_point_mm, or check the walls are roughly parallel.";
                    return null;
                }

                int chosenBIndex = 1;   // default: the two closest faces
                if (!parallel || hits.Count > 2)
                {
                    if (allowAiEscalation)
                    {
                        var pairs = new List<object>();
                        for (int k = 1; k < hits.Count && pairs.Count < 6; k++)
                            pairs.Add(new Dictionary<string, object>
                            {
                                ["indexA"] = 0,
                                ["indexB"] = k,
                                ["distanceMm"] = Math.Round(FtToMm(hits[k].Proximity - hits[0].Proximity), 1)
                            });

                        var aiChoice = AIConDecisionClient.DecideDimensionFaces(
                            wallA.Id.ToInt(), wallB.Id.ToInt(), pairs);
                        if (aiChoice != null && aiChoice.Confidence >= 0.5 && aiChoice.ChosenPairIndex + 1 < hits.Count)
                        {
                            chosenBIndex = aiChoice.ChosenPairIndex + 1;
                            note = "Multiple candidate wall faces were found along the ray; the AI decision layer chose " +
                                   "this pair (confidence " + Math.Round(aiChoice.Confidence, 2) + "). Verify in Revit.";
                        }
                        else note = "Multiple candidate wall faces were found along the ray; used the two closest faces — verify in Revit.";
                    }
                    else note = "Multiple candidate wall faces were found; used the two closest faces.";
                }

                var refArray = new ReferenceArray();
                refArray.Append(hits[0].GetReference());
                refArray.Append(hits[chosenBIndex].GetReference());

                double offsetFt = MmToFt(offsetMm);
                XYZ sideways = new XYZ(-rayDir.Y, rayDir.X, 0);
                XYZ lineStart = dimPoint + sideways * offsetFt;
                XYZ lineEnd = lineStart + rayDir * (hits[chosenBIndex].Proximity + MmToFt(200));

                try { return doc.Create.NewDimension(view, Line.CreateBound(lineStart, lineEnd), refArray); }
                catch (Exception ex)
                {
                    note = "Revit rejected these wall face references for dimensioning (" + ex.Message +
                           "). The target view may not show both walls, or the faces may not be planar there.";
                    return null;
                }
            }
            finally { if (ownsScratch) doc.Delete(tempView.Id); }
        }
    }
}
