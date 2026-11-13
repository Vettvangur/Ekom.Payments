# Publishing Payments

Payments uses release-please on `main` and a single GitHub Actions publishing
workflow, `publish-ekom-payments.yml`. Release groups are defined in
`.github/publish-projects.json`.

## Independent versions

- `Ekom.Payments` is the Core package. It takes its version from `Payments.Version.props`
  and uses tags such as `Ekom.Payments-v1.0.0`. The AspNetCore, U10, U17, U18 and U19
  packages no longer exist: Ekom owns what they did.
- Every package requires the Ekom version in `EkomVersion` (`Directory.Build.props`).
  Release Ekom first, then set `EkomVersion` to that release before publishing here.
- Each of the eight published providers has its own version in its project file,
  release-please manifest entry, changelog, and tag. For example,
  `Ekom.Payments.Netgiro-v1.0.0` publishes only Netgiro.
- All pending releases share one release PR, `chore: release main`. It lists every
  group with unreleased changes and bumps only those groups.
- ValitorPay and AsynchronousExample remain outside the publishing list.

All groups move from the `0.2.x` line to `1.0.0` (see "Moving to 1.0.0" below).
After that, release-please bumps the patch number only (`1.0.1`, `1.0.2`, ...),
independently for each group. Provider-only commits are excluded from
the shared group's release history; each provider tracks its own directory.
A commit changing multiple providers bumps each of them in the same release PR;
provider-only changes bump only that provider.

Use conventional PR titles, for example `fix(netgiro): handle callback retries`.
The files changed determine the affected release groups; the title's scope is
only descriptive. Changes to shared provider build settings outside individual
provider folders do not automatically release providers. Include a relevant
change in each affected provider directory when a new package is required.

## Moving to 1.0.0

Every package in `release-please-config.json` carries `"release-as": "1.0.0"`, so
each group's next release is `1.0.0` instead of the next `0.2.x` patch. A group only
gets released when it has unreleased changes.

`release-as` is not cleared automatically. Once a group has been released as
`1.0.0`, remove its `release-as` line from `release-please-config.json` before that
group's next change is merged; otherwise release-please proposes `1.0.0` for it
again. When all nine groups are on `1.0.x`, no `release-as` line should remain.

Providers keep their existing `CorePackageVersion` (a `0.2.x` Core) until they are
deliberately moved to a `1.0.x` Core as described below. The publishing workflow
accepts `0.2.x` and `1.0.x` tags and Core versions.

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
The release PR releases every pending group at once, so a provider change that raises
`CorePackageVersion` to an unreleased Core version must wait for a later release PR:
merge the Core change first, merge its release PR, then merge the provider change.
If a provider is released before its Core dependency is available on NuGet.org, the
provider restore fails; rerun it after Core is available.
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
   `Ekom.Payments`, workflow `publish-ekom-payments.yml`, and NuGet user `Vettvangur`,
   covering all 13 packages. No GitHub environment is used. All provider tags
   run the same workflow, so no additional workflow names are needed.
5. Merge the migration with a conventional release-worthy title, such as
   `fix: publish payment packages independently`. The bootstrap SHA records the
   pre-migration HEAD.

## Publishing and retries

Merge the release PR to tag every group it bumps. Each tag starts its own publish
run for only that group.
The workflow requires the tag, component version, and manifest to agree. It
builds on Windows using .NET 8 and .NET 10, verifies package versions and internal
dependencies, retains package/symbol artifacts, and publishes through NuGet OIDC.
Azure's private symbol server is no longer used.

Rerun a failed workflow or manually dispatch against its existing release tag.
Branch dispatches without an explicit release tag and unknown components are rejected. Duplicate NuGet versions
are skipped so an interrupted release can resume. Version-line changes require
updating both the release policy and workflow validation deliberately.

## The release PR

Release-please keeps a single release PR open on the
`release-please--branches--main` branch while any group has unreleased changes
(`separate-pull-requests` is `false` in `release-please-config.json`). Runs are
serialized and refresh the PR on every push to `main`, also when the release notes
have not changed.

Merging it releases all listed groups together; a single group cannot be held back.
To delay one group's release, leave the release PR open until that group is ready, or
keep its change out of `main`. Independent package versions and tags are preserved.

To recover a release blocked by a workflow bug, dispatch `publish-payments` using
`main` as the workflow branch and set `release_tag` to the existing tag (for
example `Ekom.Payments.TeyaConsumerloans-v0.2.98`). This uses the corrected workflow
from main while checking out and validating the original tagged package source.
Do not move the release tag. Rerunning the old failed run uses its old workflow.
