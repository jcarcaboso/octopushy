import { describe, expect, it } from "vitest";
import { bumpLevel, headlineImage, imageChanges, parseImage, shortDigest, summarizeChanges } from "./images";

describe("parseImage", () => {
  it("splits the tag after the last slash", () => {
    expect(parseImage("docker.io/skorcius/fleet-manager:0.13.0")).toEqual({
      repository: "docker.io/skorcius/fleet-manager", tag: "0.13.0", digest: null,
    });
  });

  it("does not take a registry port for a tag", () => {
    expect(parseImage("registry:5000/app")).toEqual({ repository: "registry:5000/app", tag: null, digest: null });
    expect(parseImage("registry:5000/app:1.2")).toEqual({ repository: "registry:5000/app", tag: "1.2", digest: null });
  });

  it("reads a digest, with or without a tag", () => {
    const digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    expect(parseImage(`nginx@${digest}`)).toEqual({ repository: "nginx", tag: null, digest });
    expect(parseImage(`nginx:1.27@${digest}`)).toEqual({ repository: "nginx", tag: "1.27", digest });
    expect(shortDigest(digest)).toBe("sha256:0123456789ab");
  });

  it("tolerates anything", () => {
    for (const text of ["", ":", "@", "/", "a:b:c", "::/::", "x@", "<script>"]) {
      expect(() => parseImage(text)).not.toThrow();
    }
    expect(parseImage("postgres")).toEqual({ repository: "postgres", tag: null, digest: null });
  });
});

describe("bumpLevel", () => {
  const image = (tag: string) => `docker.io/library/app:${tag}`;

  it("compares major, minor and patch", () => {
    expect(bumpLevel(image("0.12.4"), image("0.13.0"))).toBe("minor");
    expect(bumpLevel(image("1.8.2"), image("2.0.0"))).toBe("major");
    expect(bumpLevel(image("1.8.1"), image("1.8.2"))).toBe("patch");
  });

  it("tolerates a v prefix, missing parts and text around the number", () => {
    expect(bumpLevel(image("v1.2.3"), image("v1.3.0"))).toBe("minor");
    expect(bumpLevel(image("1.2.3"), image("v1.2.4"))).toBe("patch");
    expect(bumpLevel(image("17"), image("18"))).toBe("major");
    expect(bumpLevel(image("17.2"), image("17.3"))).toBe("minor");
    expect(bumpLevel(image("dashboard-0.12.4"), image("dashboard-0.13.0"))).toBe("minor");
    expect(bumpLevel(image("17.12-alpine"), image("17.13-alpine"))).toBe("minor");
  });

  it("says when a version went down", () => {
    expect(bumpLevel(image("2.0.0"), image("1.9.9"))).toBe("downgrade");
  });

  it("says new, removed and unchanged", () => {
    expect(bumpLevel(null, image("1.0"))).toBe("new");
    expect(bumpLevel(image("1.0"), null)).toBe("removed");
    expect(bumpLevel(image("1.0"), image("1.0"))).toBe("unchanged");
  });

  it("says changed when the tags cannot be compared or only the rest changed", () => {
    expect(bumpLevel(image("latest"), image("stable"))).toBe("changed");
    expect(bumpLevel(image("17.2-alpine"), image("17.2-bookworm"))).toBe("changed");
    expect(bumpLevel("docker.io/a/app:1.0", "ghcr.io/a/app:1.0")).toBe("changed");
    expect(bumpLevel(image("server-1.0"), image("worker-1.1"))).toBe("changed");
    expect(bumpLevel(image("1.0"), image("1.0.0"))).toBe("changed");
  });
});

describe("imageChanges and headlineImage", () => {
  const images = [
    { service: "db", before: "docker.io/library/postgres:17.2-alpine", after: "docker.io/library/postgres:17.2-alpine" },
    { service: "cache", before: "docker.io/valkey/valkey:8.0.1", after: "docker.io/valkey/valkey:8.0.2" },
    { service: "server", before: "docker.io/skorcius/fleet-manager:0.12.4", after: "docker.io/skorcius/fleet-manager:0.13.0" },
    { service: "proxy", before: null, after: "docker.io/library/nginx:1.27-alpine" },
  ];

  it("lists each service with its versions and level", () => {
    const changes = imageChanges(images);
    expect(changes[2]).toEqual({
      service: "server", repository: "docker.io/skorcius/fleet-manager", previousRepository: null,
      before: "0.12.4", after: "0.13.0", level: "minor",
    });
    expect(changes[3]).toMatchObject({ before: null, after: "1.27-alpine", level: "new" });
    expect(summarizeChanges(changes)).toBe("2 bumped · 1 new · 1 unchanged");
  });

  it("shows digests only when the tags read the same", () => {
    const [change] = imageChanges([{ service: "web", before: "nginx:1.27@sha256:aaaaaaaaaaaaaaaa", after: "nginx:1.27@sha256:bbbbbbbbbbbbbbbb" }]);
    expect(change).toMatchObject({ before: "1.27@sha256:aaaaaaaaaaaa", after: "1.27@sha256:bbbbbbbbbbbb", level: "changed" });
  });

  it("prefers the image named like the service, then the biggest bump", () => {
    const changes = imageChanges(images);
    expect(headlineImage(changes, "fleet")).toEqual({ change: changes[2], others: 2 });
    expect(headlineImage(changes, "other")?.change).toBe(changes[2]);
    expect(headlineImage(changes, "valkey")?.change).toBe(changes[1]);
  });

  it("has no headline when nothing changed", () => {
    expect(headlineImage(imageChanges([images[0]]), "fleet")).toBeNull();
  });
});
