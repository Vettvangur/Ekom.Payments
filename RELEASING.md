# Publishing Payments

Payments uses release-please on `main` and a single GitHub Actions publishing
workflow, `publish-payments.yml`. Release groups are defined in
`.github/publish-projects.json`.

## Independent versions

- `Ekom.Payments` groups Core, AspNetCore, U10, U17, and U18. These five packages
  share `Payments.Version.props` and use tags such as `Ekom.Payments-v0.2.98`.
- Each of the eight published providers has its own version in its project file,
  release-please manifest entry, changelog, release PR, and tag. For example,
  `Ekom.Payments.Netgiro-v0.2.98` publishes only Netgiro.
- ValitorPay and AsynchronousExample remain outside the publishing list.

All groups start at the previously verified NuGet version `0.2.97`. Each group's
next release is `0.2.98`, independently of the other groups. Release-please keeps
the existing patch-only `0.2.x` policy. Provider-only commits are excluded from
the shared group's release history; each provider tracks its own directory.
A commit changing multiple providers can produce a separate release PR for each.
The initial migration touches every provider, so it may produce one initial
release PR per provider; subsequent provider-only changes are isolated.

Use conventional PR titles, for example `fix(netgiro): handle callback retries`.
The files changed determine the affected release groups; the title's scope is
only descriptive. Changes to shared provider build settings outside individual
provider folders do not automatically release providers. Include a relevant
change in each affected provider directory when a new package is required.

## Core dependencies

Each provider declares `CorePackageVersion` separately from its own `Version`.
Local solution builds use the Core project reference. Provider publishing uses
`-p:UsePublishedCore=true` to restore and compile against the declared NuGet Core
package instead. Its restore lock file lives in `obj` so publishing does not
rewrite the normal project-reference lock file. The workflow checks the packed
Core dependency against `CorePackageVersion` before publishing.

When a provider needs a newer Core API or fix:

1. Release the shared `Ekom.Payments` group and wait for Core to be available on
   NuGet.org.
2. Update `CorePackageVersion` in that provider's project file and test with
   `UsePublishedCore=true`.
3. Merge and release the affected provider. Other providers keep their versions
   and existing minimum Core requirements.

A provider release does not consume an unreleased Core change from the checkout.
If Core and provider release PRs are merged together and the new Core dependency
is not available yet, the provider restore fails; rerun it after Core is available.
Consumers can update Core independently where it satisfies their dependencies.

## One-time cutover

1. Disable the old Azure DevOps pipeline before merging the migration so it
   cannot publish during the switch. Its YAML is removed here; disable the
   pipeline definition in Azure DevOps as well.
2. Recheck NuGet versions at cutover. If Azure has published beyond `0.2.97`,
   update the relevant manifest entries and shared/provider version files before
   running release-please. Keep provider Core dependency versions intentional.
3. Add the `RELEASE_PLEASE_TOKEN` GitHub Actions secret, as in Ekom, with contents,
   issues, and pull requests write access. A separate token lets created tags trigger
   publishing; the default `GITHUB_TOKEN` does not.
4. Use the NuGet trusted publishing policy for owner `Vettvangur`, repository
   `Ekom.Payments`, workflow `publish-payments.yml`, and NuGet user `Vettvangur`,
   covering all 13 packages. No GitHub environment is used. All provider tags
   run the same workflow, so no additional workflow names are needed.
5. Merge the migration with a conventional release-worthy title, such as
   `fix: publish payment packages independently`. The bootstrap SHA records the
   pre-migration HEAD.

## Publishing and retries

Merge a component's release PR to trigger its tag and publish only that group.
The workflow requires the tag, component version, and manifest to agree. It
builds on Windows using .NET 8 and .NET 10, verifies package versions and internal
dependencies, retains package/symbol artifacts, and publishes through NuGet OIDC.
Azure's private symbol server is no longer used.

Rerun a failed workflow or manually dispatch against its existing release tag.
Branch dispatches and unknown components are rejected. Duplicate NuGet versions
are skipped so an interrupted release can resume. Version-line changes require
updating both the release policy and workflow validation deliberately.
