# Repository editor

The **Editor** page edits any file of the lab repository inside Octopushy and pushes the
result as one commit. It follows the [architecture agreement](architecture-agreement.md#integrated-editor):
Monaco embedded in the UI, edits kept as drafts, and only Octopushy's publish path writes to Git.

## Drafts

- A draft starts from the commit Octopushy last synced on the configured branch.
- Saving (the Save button or Ctrl/Cmd+S) records the edited files as a commit on a private
  ref, `refs/octopushy/drafts/<id>`, in Octopushy's own mirror. That ref is never pushed,
  and the draft survives restarts. Content is stored literally: no Git filters or
  line-ending conversion. Existing files keep their mode, and new files are `100644`.
- Only regular UTF-8 text files up to 1 MiB are edited. Binary files, symbolic links,
  and submodules are listed but read-only.
- Several drafts can exist at once. Each save checks that the draft is still at the
  head the page loaded, so a stale tab cannot overwrite newer edits.
- If the repository URL or branch changes, existing drafts become **stale**. They can
  only be discarded.

## When the branch moves

The page shows **The branch has moved** when new commits arrive.

- **Update draft** merges them into the draft without a working tree
  (`git merge-tree --write-tree`).
- Files that conflict keep Git's conflict markers and are listed as conflicts. Each one
  counts as resolved once it is saved without markers, or deleted. Until then the draft
  cannot be published.

## Validate and publish

**Validate** loads the draft's definitions and compares them with the commit the draft is
based on. Only *new* errors fail, so an existing problem elsewhere never blocks an
unrelated edit. Changed `.json`, `.yaml`, and `.yml` files that are not definitions are
checked for well-formedness.

**Publish** asks for a commit message and an explicit confirmation, then:

1. Fetches the branch.
2. Merges the draft onto it if the branch moved. Conflicts stop the publish here and
   the draft is kept.
3. Validates the merged result.
4. Pushes a single commit with `--force-with-lease`. If the branch moves during the
   push, it retries up to three times. It never force-pushes.

The commit is authored by the signed-in operator (`<name>@octopushy.invalid`) and
committed by "Octopushy editor". A published commit is an ordinary new revision:
Octopushy loads it at the next sync and proposes the runs it implies.

## Enabling publishing

Drafts always work. Pushing is opt-in on the controller, and uses the configured
repository credential, which must be allowed to push to the branch:

```
Octopushy__RepositoryEditor__PublishEnabled=true
```

## Content Security Policy

Monaco ships with the frontend: no CDN, and its workers are files of the same origin.
It adds its theme and token colors as `<style>` elements, so the frontend's
Content-Security-Policy allows inline styles (`style-src 'self' 'unsafe-inline'`).
Scripts and workers remain limited to `'self'`. A TLS edge with its own policy, such
as the LAN preview's Caddyfile, needs the same `style-src`.

## Not yet

- A three-way merge view. Conflicts are resolved by editing the markers.
- Renaming files: delete the file and create it under the new name.
- Changing the executable bit.
- Schema-aware YAML completion.
- Separate read and write Git credentials.
