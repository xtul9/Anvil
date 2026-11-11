# Agent guide: the SoW fork of Anvil

> _There can be only one base: `net10-hot-reload`._

This fork carries Shadows Over Westgate's Anvil changes: .NET 10, plugin hot reload and name-override fixes. Upstream's `CONTRIBUTING.md` holds for code style and the changelog. The repository owner reviews, merges and tags. An agent branches, builds and opens pull requests.

## Branches

| Branch | What it is | Pull requests |
| --- | --- | --- |
| `net10-hot-reload` | The fork's work. Every `v*-sow.*` tag sits on it. | Every pull request targets this. |
| `main`, `development` | Upstream's branches. `development` is the repository default, so a bare `gh pr create` aims there. | None. The `SoW Pull Request` check fails a pull request into either. |

## A change, start to finish

1. **Branch** from the latest `v*-sow.*` tag. Read it with `git fetch --tags origin && git tag -l 'v*-sow.*' --sort=-v:refname | head -1`, then run `git switch -c <name> <tag>`. Done when `git merge-base --is-ancestor <tag> HEAD` succeeds.
2. **Build** with `dotnet build --configuration Release`. Done when it reports `0 Error(s)`. If it fails, fix the change and build again. Don't open the pull request on a red build.
3. **Open the pull request**: `gh pr create --repo xtul9/Anvil --base net10-hot-reload --head <name> --title "<what it changes>" --body-file <file>`. Always pass `--base`. Done when `gh pr view <n> --repo xtul9/Anvil --json changedFiles -q .changedFiles` matches the file count of `git diff --stat <tag>...HEAD`. On a mismatch, close it and open it again with the right base.
4. **Write the body** with these three sections, in plain prose:
   - `## What this fixes` (or `## What this adds`): the problem in the player's or plugin author's terms, and its root cause.
   - `## Change`: what the code now does, and the files it touches.
   - `## Checked`: each command you ran and its result, for example the build line above. List only commands you ran on this branch.

   Done when every claim in the body comes from this branch's diff or a command you ran.
5. **Check CI**: `gh pr checks <n> --repo xtul9/Anvil --watch`. Done when `Base is net10-hot-reload` and `Release build` both pass. If one fails, read its log with `gh run view <run> --repo xtul9/Anvil --log-failed`, then fix and push.
6. **Stop.** The owner reviews and merges. A pull request merged by its author, or into another base, has to be reverted. Done when the owner has merged it, or has left a comment for you to answer.

## Releases

The owner tags a merge on `net10-hot-reload` with the next `v<anvil version>-sow.<n>`. The tag runs `.github/workflows/sow-release.yml`, which refuses a tag that is not on `net10-hot-reload`. It then builds both packages, pushes them to the SoW feed and publishes the image. An agent pushes no tags. Done when the run is green and the image tag lists in the registry.

## Tests

Upstream's test job runs the suite inside a live server in its devcontainer (see the `test` job in `.github/workflows/ci.yml`). It runs only on upstream's branches. To run it on a fork change, use that devcontainer locally and say in the body that you did.
