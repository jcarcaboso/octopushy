# Service icons

The Services page shows an icon for each service. It is the first of these that exists:

1. **A picture uploaded for the service** on the Services page.
2. **A file chosen for it** from the icons directory. A chosen file that is no longer there is skipped.
3. **The file named after the service** in the icons directory: `<name>.svg`, `.png`, `.webp`, `.jpg` or `.jpeg`, tried
   in that order. A service `web` gets `web.svg` with no setup.
4. Otherwise the page draws the service's first letter.

Removing a service's icon on the page drops its picture or chosen file, and it goes back to the file named after it.

**Decided by the owner on November 13, 2026:** an uploaded picture is kept in Octopushy's database, not in the lab
repository. It is used only on the Services page and has nothing to do with deploying the service. This is the one
exception to [changes from the UI](changes-from-the-ui.md), where everything a form changes is a commit.

## The icons directory

Octopushy ships no icons. The directory is `icons` under the data directory (`/var/lib/octopushy/icons` in the
[Compose project](../deploy/compose.yaml)); `Octopushy:Icons:Directory` (`Octopushy__Icons__Directory`) puts it elsewhere.
It may be missing or empty, and then there are no files to choose from.

Map a folder of logos you already have, such as one on a NAS, into the backend read-only:

```yaml
  backend:
    volumes:
      - data:/var/lib/octopushy
      - /srv/icons:/var/lib/octopushy/icons:ro
```

Only files directly in it count, not those in subfolders, and not links. A name starts with a letter or digit, has at
most 128 characters of letters, digits, `.`, `_` and `-`, and ends in `.svg`, `.png`, `.webp`, `.jpg` or `.jpeg`. The
backend reads the directory again at most every 30 seconds, so a file added there shows up within half a minute.

## Pictures

A picture is a PNG, JPEG, WebP or SVG image of at most 1 MiB. Its bytes must be what its type says: an SVG must be XML
with an `svg` root element and no DTD. It is kept as it was sent, neither resized nor converted. Pictures live in the
database, so a database backup holds them.

## Where they load from

Every icon is served by Octopushy itself, under `/api/icons/` and `/api/services/<id>/icon`, to signed-in operators only.
Nothing is loaded from other hosts: no favicons and no icon URLs. An icon opened on its own is served as a sandboxed
document that runs no script, so an SVG cannot act in Octopushy's pages.
