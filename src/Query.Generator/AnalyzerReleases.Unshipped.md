; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
NWQ0101 | NativeWeave.Query | Error | No enabled native codec; use an explicit supported static mapping.
NWQ0102 | NativeWeave.Query | Error | Nullable database mapping targets a non-null constructor or key.
NWQ0103 | NativeWeave.Query | Error | Ambiguous or inaccessible static constructor/member mapping.
NWQ0104 | NativeWeave.Query | Error | Duplicate or inconsistent metadata, alias or key declaration.
NWQ0105 | NativeWeave.Query | Error | Required codec absent from the explicit compile-time package profile.
NWQ0106 | NativeWeave.Query | Error | Unsupported generator declaration or dependency shape.
NWQ0107 | NativeWeave.Query | Error | Mapping requires dynamic code or unbounded runtime type discovery.
