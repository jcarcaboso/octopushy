# Resource detail prototypes

Sixteen throwaway design explorations for the page of one node (a Proxmox installation), one
machine (a guest) or one service. Today that page leads with the YAML definition in a code box;
none of these does, and each says where the YAML went. Open `index.html` in a browser (no server
or build needed); each prototype is one self-contained HTML file, and `?kind=node`, `?kind=machine`
or `?kind=service` picks what it shows.

All of them read `kit.js`: sample data for `home/moraband`, `home/moraband/falcon` and
`home/moraband/falcon/open-webui`, shaped like what the API returns today for an installation, a
guest and a service, with addresses and domains changed. The machine is healthy, the service has a
deploy waiting for approval, and the node has a warning, so each design shows a quiet page and a
page that needs someone. A definition is there twice: as its YAML, and as sections of fields that
each know their line in it.

Values the API does not have are under a key named `sample` (a guest's live CPU, memory and disk
use, a node's CPU model and datastore use). Four prototypes use some of them and say so in their top
comment: 02 Tabs, 03 Declared and observed, 06 Blueprint and 07 Source map.

`kit.css` is the app's shell and base components; the colors come straight from
`frontend/src/styles/tokens.css`, so the prototypes follow the app's themes. `nav.js` is the switcher
in the bottom-right corner.

None of this is wired to the backend. The pages that grew out of them, from 01 Spec sheet, are
`GuestPage.tsx`, `ServicePage.tsx` and `InstallationPage.tsx` in `frontend/src/pages`. 01 also shows
what the pages do not do yet: a value a waiting run would change, marked where it stands in the sheet.
