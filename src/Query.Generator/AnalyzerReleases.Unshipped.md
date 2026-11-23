; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
NWQ0101 | Korda.Query | Error | No enabled native codec; use an explicit supported static mapping.
NWQ0102 | Korda.Query | Error | Nullable database mapping targets a non-null constructor or key.
NWQ0103 | Korda.Query | Error | Ambiguous or inaccessible static constructor/member mapping.
NWQ0104 | Korda.Query | Error | Duplicate or inconsistent metadata, alias or key declaration.
NWQ0105 | Korda.Query | Error | Required codec absent from the explicit compile-time package profile.
NWQ0106 | Korda.Query | Error | Unsupported generator declaration or dependency shape.
NWQ0107 | Korda.Query | Error | Mapping requires dynamic code or unbounded runtime type discovery.
