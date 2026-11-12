# Inventory prototypes

Ten throwaway design explorations for the Inventory page. Open `index.html` in a browser
(no server or build needed); each prototype is one self-contained HTML file.

All of them read `data.js`. Its hosts, guests, services and DNS records are modelled on a
real lab, with its domains and addresses changed. The node resources, run status, uptime
history and unmanaged VMs are made up so there is something to show. `nav.js` is
the switcher in the bottom-right corner.

None of this is wired to the backend. The Inventory page that grew out of them, the columns
one, is in `frontend/src/pages/InventoryPage.tsx`, with uptime from `GET /api/uptime`.
