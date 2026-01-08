#region Namespaces
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
#endregion

namespace TraverseAllSystems
{
    /// <summary>
    /// AquaBrain Sprinkler System Analyzer
    /// Analyzes fire sprinkler systems for NFPA 13 / TI 1596 compliance
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SprinklerSystemAnalyzer : IExternalCommand
    {
        // NFPA 13 Spacing Requirements (in feet, converted to internal units)
        private const double MinSpacingFeet = 6.0;   // 1.8m minimum
        private const double MaxSpacingFeet = 15.0;  // 4.6m maximum (Light Hazard)

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // Collect all sprinkler instances
                var sprinklers = CollectSprinklers(doc);

                if (sprinklers.Count == 0)
                {
                    TaskDialog.Show("Sprinkler Analysis",
                        "No sprinkler family instances found in the model.");
                    return Result.Succeeded;
                }

                // Collect piping systems (fire protection)
                var sprinklerSystems = CollectSprinklerPipingSystems(doc);

                // Analyze each system
                var analysisResults = new SprinklerAnalysisReport
                {
                    ModelName = doc.Title,
                    AnalysisDate = DateTime.Now,
                    TotalSprinklers = sprinklers.Count,
                    TotalSystems = sprinklerSystems.Count,
                    Systems = new List<SystemAnalysis>()
                };

                foreach (var system in sprinklerSystems)
                {
                    var systemAnalysis = AnalyzeSystem(doc, system, sprinklers);
                    analysisResults.Systems.Add(systemAnalysis);
                }

                // Perform spacing analysis
                var spacingViolations = AnalyzeSpacing(sprinklers);
                analysisResults.SpacingViolations = spacingViolations;
                analysisResults.ComplianceRate = CalculateComplianceRate(sprinklers.Count, spacingViolations.Count);

                // Export to JSON
                string outputPath = ExportResults(doc, analysisResults);

                // Show summary
                ShowResultDialog(analysisResults, outputPath);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Error analyzing sprinkler systems: {ex.Message}";
                return Result.Failed;
            }
        }

        /// <summary>
        /// Collects all sprinkler family instances in the document
        /// </summary>
        private List<SprinklerData> CollectSprinklers(Document doc)
        {
            var sprinklers = new List<SprinklerData>();

            // Get all family instances
            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_Sprinklers);

            foreach (FamilyInstance fi in collector)
            {
                var locationPoint = fi.Location as LocationPoint;
                if (locationPoint == null) continue;

                var sprinkler = new SprinklerData
                {
                    ElementId = fi.Id.IntegerValue,
                    UniqueId = fi.UniqueId,
                    FamilyName = fi.Symbol?.FamilyName ?? "Unknown",
                    TypeName = fi.Symbol?.Name ?? "Unknown",
                    Location = new XYZData
                    {
                        X = locationPoint.Point.X,
                        Y = locationPoint.Point.Y,
                        Z = locationPoint.Point.Z
                    }
                };

                // Get level
                var level = doc.GetElement(fi.LevelId) as Level;
                if (level != null)
                {
                    sprinkler.LevelName = level.Name;
                    sprinkler.LevelElevation = level.Elevation;
                }

                // Try to get K-Factor parameter
                sprinkler.KFactor = GetParameterDouble(fi, "K-Factor", "K_Factor", "KFactor");

                // Try to get coverage area
                sprinkler.CoverageArea = GetParameterDouble(fi, "Coverage Area", "Coverage_Area");

                // Determine orientation from family name
                sprinkler.Orientation = DetermineOrientation(fi);

                sprinklers.Add(sprinkler);
            }

            return sprinklers;
        }

        /// <summary>
        /// Collects fire protection piping systems
        /// </summary>
        private List<PipingSystem> CollectSprinklerPipingSystems(Document doc)
        {
            var systems = new List<PipingSystem>();

            var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystem));

            foreach (PipingSystem ps in collector)
            {
                // Filter for fire protection systems by name pattern
                // Revit 2025+ uses different classification approach
                string systemName = ps.Name?.ToLower() ?? "";
                string typeName = ps.SystemType.ToString().ToLower();

                bool isFireProtection =
                    systemName.Contains("fire") ||
                    systemName.Contains("sprinkler") ||
                    typeName.Contains("fire") ||
                    typeName.Contains("sprinkler");

                if (isFireProtection && ps.Elements.Size > 0)
                {
                    systems.Add(ps);
                }
            }

            return systems;
        }

        /// <summary>
        /// Analyzes a single piping system
        /// </summary>
        private SystemAnalysis AnalyzeSystem(Document doc, PipingSystem system, List<SprinklerData> allSprinklers)
        {
            var analysis = new SystemAnalysis
            {
                SystemId = system.Id.IntegerValue,
                SystemName = system.Name,
                SystemType = system.SystemType.ToString(),
                ElementCount = system.Elements.Size,
                Sprinklers = new List<int>(),
                Pipes = new List<PipeData>(),
                Fittings = new List<int>()
            };

            // Traverse system elements
            foreach (Element elem in system.Elements)
            {
                if (elem is FamilyInstance fi)
                {
                    if (fi.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_Sprinklers)
                    {
                        analysis.Sprinklers.Add(elem.Id.IntegerValue);
                    }
                    else if (fi.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_PipeFitting)
                    {
                        analysis.Fittings.Add(elem.Id.IntegerValue);
                    }
                }
                else if (elem is Pipe pipe)
                {
                    var pipeData = new PipeData
                    {
                        ElementId = pipe.Id.IntegerValue,
                        Diameter = pipe.Diameter,
                        Length = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0
                    };
                    analysis.Pipes.Add(pipeData);
                    analysis.TotalPipeLength += pipeData.Length;
                }
            }

            analysis.SprinklerCount = analysis.Sprinklers.Count;

            // Build graph structure if well connected
            if (system.IsWellConnected)
            {
                analysis.IsWellConnected = true;
                try
                {
                    var tree = new TraversalTree(system);
                    if (tree.Traverse())
                    {
                        analysis.GraphJson = tree.DumpToJsonTopDown();
                    }
                }
                catch
                {
                    // Graph traversal failed, skip
                }
            }

            return analysis;
        }

        /// <summary>
        /// Analyzes sprinkler spacing for NFPA 13 compliance
        /// </summary>
        private List<SpacingViolation> AnalyzeSpacing(List<SprinklerData> sprinklers)
        {
            var violations = new List<SpacingViolation>();
            var checkedPairs = new HashSet<string>();

            // Convert feet to internal units (feet in Revit)
            double minSpacing = MinSpacingFeet;
            double maxSpacing = MaxSpacingFeet;

            foreach (var sprinkler in sprinklers)
            {
                // Find neighbors on same level (within 1.5 ft vertical tolerance)
                var neighbors = sprinklers
                    .Where(s => s.ElementId != sprinkler.ElementId)
                    .Where(s => Math.Abs(s.Location.Z - sprinkler.Location.Z) < 1.5)
                    .Select(s => new { Sprinkler = s, Distance = HorizontalDistance(sprinkler.Location, s.Location) })
                    .Where(x => x.Distance < maxSpacing * 2)
                    .OrderBy(x => x.Distance)
                    .Take(4);

                foreach (var neighbor in neighbors)
                {
                    // Create unique pair key
                    int id1 = Math.Min(sprinkler.ElementId, neighbor.Sprinkler.ElementId);
                    int id2 = Math.Max(sprinkler.ElementId, neighbor.Sprinkler.ElementId);
                    string pairKey = $"{id1}_{id2}";

                    if (checkedPairs.Contains(pairKey))
                        continue;
                    checkedPairs.Add(pairKey);

                    if (neighbor.Distance < minSpacing)
                    {
                        violations.Add(new SpacingViolation
                        {
                            Sprinkler1Id = sprinkler.ElementId,
                            Sprinkler2Id = neighbor.Sprinkler.ElementId,
                            ActualSpacingFeet = neighbor.Distance,
                            ActualSpacingMeters = neighbor.Distance * 0.3048,
                            RequiredMinFeet = minSpacing,
                            RequiredMaxFeet = maxSpacing,
                            ViolationType = "TooClose",
                            LevelName = sprinkler.LevelName
                        });
                    }
                    else if (neighbor.Distance > maxSpacing)
                    {
                        violations.Add(new SpacingViolation
                        {
                            Sprinkler1Id = sprinkler.ElementId,
                            Sprinkler2Id = neighbor.Sprinkler.ElementId,
                            ActualSpacingFeet = neighbor.Distance,
                            ActualSpacingMeters = neighbor.Distance * 0.3048,
                            RequiredMinFeet = minSpacing,
                            RequiredMaxFeet = maxSpacing,
                            ViolationType = "TooFar",
                            LevelName = sprinkler.LevelName
                        });
                    }
                }
            }

            return violations;
        }

        private double HorizontalDistance(XYZData p1, XYZData p2)
        {
            double dx = p1.X - p2.X;
            double dy = p1.Y - p2.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private double CalculateComplianceRate(int totalSprinklers, int violationCount)
        {
            if (totalSprinklers == 0) return 100.0;
            // Estimate unique sprinklers involved in violations
            int affectedSprinklers = Math.Min(violationCount, totalSprinklers);
            return Math.Round((1.0 - (double)affectedSprinklers / totalSprinklers) * 100, 1);
        }

        private string ExportResults(Document doc, SprinklerAnalysisReport results)
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AquaBrain",
                "SprinklerAnalysis");

            Directory.CreateDirectory(folder);

            string filename = $"{doc.Title}_SprinklerAnalysis_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string fullPath = Path.Combine(folder, filename);

            string json = JsonConvert.SerializeObject(results, Formatting.Indented);
            File.WriteAllText(fullPath, json);

            return fullPath;
        }

        private void ShowResultDialog(SprinklerAnalysisReport results, string outputPath)
        {
            int tooClose = results.SpacingViolations.Count(v => v.ViolationType == "TooClose");
            int tooFar = results.SpacingViolations.Count(v => v.ViolationType == "TooFar");

            string msg = $"Sprinkler Analysis Complete\n\n" +
                $"Total Sprinklers: {results.TotalSprinklers}\n" +
                $"Total Systems: {results.TotalSystems}\n" +
                $"Compliance Rate: {results.ComplianceRate}%\n\n" +
                $"Spacing Violations:\n" +
                $"  Too Close (< {MinSpacingFeet}ft): {tooClose}\n" +
                $"  Too Far (> {MaxSpacingFeet}ft): {tooFar}\n\n" +
                $"Results exported to:\n{outputPath}";

            TaskDialog.Show("AquaBrain Sprinkler Analysis", msg);
        }

        private double GetParameterDouble(FamilyInstance fi, params string[] paramNames)
        {
            foreach (string name in paramNames)
            {
                var param = fi.LookupParameter(name);
                if (param != null && param.HasValue)
                {
                    if (param.StorageType == StorageType.Double)
                        return param.AsDouble();
                    if (param.StorageType == StorageType.Integer)
                        return param.AsInteger();
                }

                // Try type parameter
                var symbol = fi.Symbol;
                if (symbol != null)
                {
                    param = symbol.LookupParameter(name);
                    if (param != null && param.HasValue)
                    {
                        if (param.StorageType == StorageType.Double)
                            return param.AsDouble();
                        if (param.StorageType == StorageType.Integer)
                            return param.AsInteger();
                    }
                }
            }
            return 0;
        }

        private string DetermineOrientation(FamilyInstance fi)
        {
            string familyName = fi.Symbol?.FamilyName?.ToLower() ?? "";
            string typeName = fi.Symbol?.Name?.ToLower() ?? "";
            string combined = familyName + " " + typeName;

            if (combined.Contains("pendent") || combined.Contains("pendant"))
                return "Pendent";
            if (combined.Contains("upright"))
                return "Upright";
            if (combined.Contains("sidewall"))
                return "Sidewall";
            if (combined.Contains("concealed"))
                return "Concealed";
            if (combined.Contains("recessed"))
                return "Recessed";

            return "Pendent"; // Default
        }
    }

    #region Data Classes

    public class SprinklerAnalysisReport
    {
        public string ModelName { get; set; }
        public DateTime AnalysisDate { get; set; }
        public int TotalSprinklers { get; set; }
        public int TotalSystems { get; set; }
        public double ComplianceRate { get; set; }
        public List<SystemAnalysis> Systems { get; set; }
        public List<SpacingViolation> SpacingViolations { get; set; }
    }

    public class SystemAnalysis
    {
        public int SystemId { get; set; }
        public string SystemName { get; set; }
        public string SystemType { get; set; }
        public int ElementCount { get; set; }
        public int SprinklerCount { get; set; }
        public bool IsWellConnected { get; set; }
        public double TotalPipeLength { get; set; }
        public List<int> Sprinklers { get; set; }
        public List<PipeData> Pipes { get; set; }
        public List<int> Fittings { get; set; }
        public string GraphJson { get; set; }
    }

    public class SprinklerData
    {
        public int ElementId { get; set; }
        public string UniqueId { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public XYZData Location { get; set; }
        public string LevelName { get; set; }
        public double LevelElevation { get; set; }
        public double KFactor { get; set; }
        public double CoverageArea { get; set; }
        public string Orientation { get; set; }
    }

    public class XYZData
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public class PipeData
    {
        public int ElementId { get; set; }
        public double Diameter { get; set; }
        public double Length { get; set; }
    }

    public class SpacingViolation
    {
        public int Sprinkler1Id { get; set; }
        public int Sprinkler2Id { get; set; }
        public double ActualSpacingFeet { get; set; }
        public double ActualSpacingMeters { get; set; }
        public double RequiredMinFeet { get; set; }
        public double RequiredMaxFeet { get; set; }
        public string ViolationType { get; set; }
        public string LevelName { get; set; }
    }

    #endregion
}
