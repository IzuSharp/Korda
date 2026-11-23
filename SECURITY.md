# Security policy

Security fixes target the current development branch and supported public packages. Preview packages carry no long-term support guarantee.

## Report privately

Use [GitHub private vulnerability reporting](https://github.com/IzuSharp/Korda/security/advisories/new) when available. Include the affected package and version or commit, platform, impact and steps to reproduce. If private reporting is unavailable, request a private maintainer contact without posting credentials or vulnerability details.

## Database packages

The PostgreSQL and SQLite providers load native libraries into the application process. Install packages from a trusted source. The packages include dependency notices and asset manifests. Treat connection strings and database credentials as secrets.

Pull requests validate affected components and Native AOT consumers. The release workflow verifies eligible candidate archives before using short-lived NuGet trusted publishing credentials in the `nuget` environment. The [release guide](docs/releasing.md) describes signed registry consumption and the [workflows](.github/workflows/) define the security checks.
