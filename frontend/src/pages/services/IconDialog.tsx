import { useState, type ChangeEvent } from "react";
import { useChangeServiceIcon, useIcons, type IconChange } from "../../api/queries";
import type { ServiceSummary } from "../../api/types";
import { Dialog } from "../../components/Dialog";
import { ServiceIcon } from "../../components/ServiceIcon";
import { ErrorMessage, Spinner } from "../../components/States";

// The server's limits for an uploaded picture.
const pictureTypes = ["image/png", "image/jpeg", "image/webp", "image/svg+xml"];
const maxPicture = 1024 * 1024;
const shown = 60;

const fileOf = (url: string) => decodeURIComponent(url.split("?")[0].split("/").pop() ?? "");
const withoutExtension = (name: string) => name.replace(/\.[^.]+$/, "");

function origin(icon: ServiceSummary["icon"]): string {
  if (!icon) return "No icon: the page shows its first letter";
  if (icon.source === "picture") return "Uploaded picture";
  if (icon.source === "chosen") return `Chosen: ${fileOf(icon.url)}`;
  return `Matches the service's name: ${fileOf(icon.url)}`;
}

/** Rendered only while open, so every opening starts with an empty search and no error. */
export function IconDialog({ service, onClose }: { service: ServiceSummary; onClose: () => void }) {
  const icons = useIcons();
  const change = useChangeServiceIcon(service.id);
  const [search, setSearch] = useState("");
  const [refused, setRefused] = useState<string | null>(null);

  const apply = (next: IconChange) => {
    setRefused(null);
    change.mutate(next, { onSuccess: onClose });
  };

  function pick(event: ChangeEvent<HTMLInputElement>) {
    const file = event.currentTarget.files?.[0];
    // Cleared, so choosing the same file again after a refusal still fires.
    event.currentTarget.value = "";
    if (!file) return;
    if (!pictureTypes.includes(file.type)) setRefused("That is not a PNG, JPEG, WebP or SVG image.");
    else if (file.size > maxPicture) setRefused("That picture is larger than 1 MiB.");
    else apply({ picture: file });
  }

  const names = icons.data?.names ?? [];
  const q = search.trim().toLowerCase();
  const matches = q ? names.filter((n) => n.toLowerCase().includes(q)) : names;
  const own = service.icon?.source === "picture" || service.icon?.source === "chosen";

  return (
    <Dialog open onClose={onClose} title={`Icon for ${service.name}`}>
      <div className="stack stack--large">
        <div className="inline-group">
          <ServiceIcon service={service} large />
          <span>{origin(service.icon)}</span>
          {own ? (
            <button type="button" className="button button--small" disabled={change.isPending} onClick={() => apply("default")}>
              Use the default
            </button>
          ) : null}
        </div>
        <ErrorMessage error={change.error} />

        <section className="field" aria-labelledby="icon-predefined">
          <h3 id="icon-predefined" className="field__label">
            Predefined
          </h3>
          {icons.isPending ? (
            <Spinner />
          ) : !icons.data ? (
            <ErrorMessage error={icons.error} title="The icons could not be loaded" />
          ) : names.length === 0 ? (
            <p className="muted small">No predefined icons. Map a folder of images onto the backend's icons directory to offer some.</p>
          ) : (
            <div className="stack stack--small">
              <input type="search" aria-label="Find an icon" placeholder="Find an icon" value={search} onChange={(e) => setSearch(e.target.value)} data-autofocus />
              <ul className="icon-grid">
                {matches.slice(0, shown).map((name) => (
                  <li key={name}>
                    <button type="button" className="icon-grid__item" aria-label={name} title={name} disabled={change.isPending} onClick={() => apply({ predefined: name })}>
                      <img className="service-icon service-icon--large" src={`/api/icons/${encodeURIComponent(name)}`} alt="" loading="lazy" />
                      <span className="icon-grid__name small">{withoutExtension(name)}</span>
                    </button>
                  </li>
                ))}
              </ul>
              {matches.length > shown ? <p className="muted small">{matches.length - shown} more: keep typing</p> : null}
              {matches.length === 0 ? <p className="muted small">No icon matches.</p> : null}
            </div>
          )}
        </section>

        <div className="field">
          <label htmlFor="icon-picture">Picture</label>
          <input
            id="icon-picture"
            type="file"
            accept=".png,.jpg,.jpeg,.webp,.svg"
            disabled={change.isPending}
            onChange={pick}
            aria-invalid={refused ? true : undefined}
            aria-describedby="icon-picture-hint"
          />
          <div id="icon-picture-hint" className={refused ? "field__error" : "field__hint"}>
            {refused ?? "PNG, JPEG, WebP or SVG, up to 1 MiB"}
          </div>
        </div>
      </div>
    </Dialog>
  );
}
