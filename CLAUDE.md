# CLAUDE.md - TraverseAllSystems

## Project Overview

TraverseAllSystems is a C# Revit API add-in for extracting MEP system graph structures. Originally developed by Jeremy Tammik (The Building Coder), extended by AquaBrain for fire sprinkler system analysis.

## Repository Structure

```
TraverseAllSystems/
├── TraverseAllSystems/
│   ├── TraverseAllSystems.csproj    # Multi-target 2025/2026 project
│   ├── Command.cs                    # Main IExternalCommand entry point
│   ├── TraversalTree.cs              # System graph traversal logic
│   ├── SprinklerSystemAnalyzer.cs    # AquaBrain: Sprinkler analysis
│   ├── Options.cs                    # JSON export options
│   ├── SharedParameterMgr.cs         # Shared parameter handling
│   ├── Util.cs                       # Utility functions
│   ├── TraverseAllSystems_2025.addin # Revit 2025 add-in manifest
│   ├── TraverseAllSystems_2026.addin # Revit 2026 add-in manifest
│   └── packages.config               # NuGet: Newtonsoft.Json
└── README.md                         # Original documentation
```

## Build Configurations

| Configuration | Target | Output |
|--------------|--------|--------|
| Debug2025 | Revit 2025 | bin/Debug2025/ |
| Release2025 | Revit 2025 | bin/Release2025/ |
| Debug2026 | Revit 2026 | bin/Debug2026/ |
| Release2026 | Revit 2026 | bin/Release2026/ |

## Build Commands

```bash
# From Windows (cmd/PowerShell)
msbuild TraverseAllSystems.csproj /p:Configuration=Debug2025
msbuild TraverseAllSystems.csproj /p:Configuration=Release2025

# Or via Visual Studio
# Select configuration from dropdown, then Build > Build Solution
```

## Post-Build Deployment

The project automatically deploys to Revit add-ins folder:
```
%AppData%\Autodesk\REVIT\Addins\2025\TraverseAllSystems.addin
%AppData%\Autodesk\REVIT\Addins\2025\TraverseAllSystems.dll
```

## Key Classes

### Command.cs
Main entry point implementing `IExternalCommand`. Collects all MEP systems and exports graph structure.

```csharp
// Filter predicate for desirable systems
static bool IsDesirableSystemPredicate(MEPSystem s)
{
    return s.Elements.Size > 1
        && !s.Name.Equals("unassigned")
        && (s is MechanicalSystem && ((MechanicalSystem)s).IsWellConnected
         || s is PipingSystem && ((PipingSystem)s).IsWellConnected
         || s is ElectricalSystem && ((ElectricalSystem)s).IsMultipleNetwork);
}
```

### TraversalTree.cs
Handles graph traversal using Revit's `TraversePipe()` method for piping systems.

### SprinklerSystemAnalyzer.cs (AquaBrain Extension)
Specialized analysis for fire sprinkler systems:
- NFPA 13 / TI 1596 compliance checking
- Sprinkler spacing validation
- Coverage area analysis
- System branch analysis

## JSON Output Format

### Bottom-Up (jsTree compatible)
```json
[
  { "id": "ajson1", "parent": "#", "text": "Root Node" },
  { "id": "ajson2", "parent": "ajson1", "text": "Child 1" }
]
```

### Top-Down (Hierarchical)
```json
{
  "id": 1,
  "name": "MEP Systems",
  "children": [
    {
      "id": 2,
      "name": "Piping System",
      "children": [...]
    }
  ]
}
```

## MEP Domains

```csharp
public enum MepDomain
{
    Invalid = -1,
    Mechanical = 0,  // HVAC duct systems
    Electrical = 1,  // Power/data systems
    Piping = 2,      // Plumbing, fire protection, hydronic
    Count = 3
}
```

## AquaBrain Integration

### Sprinkler System Types
- Wet pipe systems
- Dry pipe systems
- Preaction systems
- Deluge systems

### Analysis Outputs
- System graph JSON with sprinkler data
- Spacing violation reports
- Branch pipe analysis
- Hydraulic demand points

## Dependencies

- Revit API 2025/2026 (RevitAPI.dll, RevitAPIUI.dll)
- Newtonsoft.Json 13.0.3 (for JSON serialization)
- .NET Framework 4.8

## Testing

1. Open a Revit model with MEP systems
2. Run the "Traverse MEP Systems" command from Add-ins tab
3. Check output folder for XML/JSON files
4. Verify graph structure in output dialog

## Original Author

Jeremy Tammik, The Building Coder
- Blog: http://thebuildingcoder.typepad.com
- GitHub: https://github.com/jeremytammik/TraverseAllSystems

## License

MIT License - See LICENSE file
