# Compilation Fixes Checklist

This document records the fixes for Visual Studio 2022+ compilation errors.

## Fixed Issues

### 1. HIGH PRIORITY: Duplicate Assembly Attributes

**Problem Description:**
- Compilation errors due to duplicate Assembly* / TargetFrameworkAttribute attributes
- Issue: MSBuild-generated temporary AssemblyInfo in obj directory conflicts with manually written AssemblyInfo.cs

**Fix:**
Added to `币安量化机器人.csproj`:
```xml
<GenerateAssemblyInfo>false</GenerateAssemblyInfo>
```

**File:** `币安量化机器人.csproj`

**Status:** ✅ Complete

---

### 2. MEDIUM PRIORITY: Missing EnumeratorCancellation Attribute

**Problem Description:**
- Warning CS8425: Async iterator methods contain CancellationToken parameter but lack `[EnumeratorCancellation]` attribute
- Impact: Caller's cancellation token won't automatically propagate to iterator

**Fix:**
Added `[EnumeratorCancellation]` attribute to CancellationToken parameters in async iterators:

```csharp
using System.Runtime.CompilerServices;

public async IAsyncEnumerable<RawDataFrame> ReadAsync(
    DataQuery query, 
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    // ... implementation
}
```

**Fixed Files:**
- ✅ `Infrastructure/Data/DatabaseDataSource.cs`
- ✅ `Infrastructure/Data/ApiDataSource.cs`
- ✅ `Infrastructure/Data/FileDataSource.cs`

**Status:** ✅ Complete

---

### 3. MEDIUM PRIORITY: ScottPlot 5.x API Compatibility

**Problem Description:**
- Code uses legacy ScottPlot API (`plt.XLabel()`, `plt.YLabel()`)
- ScottPlot 5.x replaced these methods with properties

**Fix:**
Changed method calls to property assignments:

```csharp
// Old API (incorrect)
plt.XLabel("X Label");
plt.YLabel("Y Label");

// New API (correct)
plt.Axes.Bottom.Label.Text = "X Label";
plt.Axes.Left.Label.Text = "Y Label";
```

**Fixed Files:**
- ✅ `Modules/Optimize/HeatmapView.xaml.cs`

**Verified Files (already correct):**
- ✅ `Modules/Optimize/WfoOptimizer.xaml.cs`
- ✅ `Modules/Market/RealtimeView.xaml.cs`
- ✅ `Modules/Market/FundingView.xaml.cs`

**Status:** ✅ Complete

---

## Low Priority Items

### 4. Nullable Reference Type Warnings

**Description:**
- Warnings CS8618 / CS8625: Non-nullable properties/fields not initialized
- These are warnings and won't block compilation, but should be addressed in future iterations

**Suggested Fixes:**
1. Provide default values: `public string Name { get; set; } = string.Empty;`
2. Mark as nullable: `public string? Name { get; set; }`
3. Use `required` modifier (C# 11+)

**Status:** ⏳ Pending (doesn't block compilation)

---

### 5. Decimal/Double Type Mixing

**Description:**
- Mixed use of `decimal` and `double` may cause precision issues
- Recommend standardizing on `decimal` for financial domain

**Status:** ⏳ Pending (code quality improvement)

---

## Verification Steps

### Verify compilation in Windows environment:

```bash
# 1. Restore NuGet packages
dotnet restore 币安量化机器人.csproj

# 2. Clean build
dotnet clean 币安量化机器人.csproj

# 3. Build project
dotnet build 币安量化机器人.csproj -c Debug

# 4. Check for errors
# Should only have warnings, no errors
```

### Verify in Visual Studio 2022:

1. Open `币安量化机器人.slnx` or `币安量化机器人.csproj`
2. Clean Solution
3. Rebuild Solution
4. Confirm successful build with no errors

---

## Project Configuration Summary

**Target Framework:** .NET 8.0 (net8.0-windows)  
**Output Type:** WinExe (Windows Desktop App)  
**UI Framework:** WPF  
**Key NuGet Packages:**
- ScottPlot.WPF 5.0.56
- Binance.Net 8.3.0
- Microsoft.Data.Sqlite 8.0.4

**Enabled Features:**
- Nullable reference types
- Implicit usings
- Disabled auto-generated AssemblyInfo

---

## Known Limitations

1. **Platform Limitation:** This project requires Windows platform (net8.0-windows)
2. **Cross-platform Build:** Building on Linux/macOS requires setting `EnableWindowsTargeting=true`
3. **Runtime Environment:** Requires .NET 8.0 Desktop Runtime

---

## Contact & Feedback

If you encounter other compilation issues:
1. Check .NET SDK version (requires 8.0 or higher)
2. Verify all NuGet packages are properly restored
3. Clean obj and bin directories before rebuilding
4. Report issues in GitHub Issues

---

**Last Updated:** 2025-11-18  
**Fix Version:** commit eeabc24
