# Integrated repository editor options

**Reviewed: October 14, 2026.** Scope: ways to offer GitHub-style in-browser
editing (press `.` on a repository) inside Octopushy, so that edits become
drafts that Octopushy validates and commits. Documentation review only; nothing
was built or tested. The recommendation is an engineering judgment, not a
decision.

## How github.dev works

github.dev "runs entirely in your browser's sandbox". It does not clone the
repository; an extension reads files through GitHub's API, and unsaved work stays
in browser local storage until committed. There is no terminal or code
execution, and only web-compatible extensions are available. [E1]

The useful pattern is a **virtual workspace**: the editor sees files through a
provider backed by an API, not through a server-side checkout and shell.

## Options

| Option | What runs | Strengths | Costs and risks |
|---|---|---|---|
| **A. Embedded Monaco editor** | Monaco, VS Code's editor component, inside Octopushy's own UI | Self-contained static assets; edits go straight into drafts; schema-aware YAML validation, diff, and merge views can be built in | Not the full VS Code workbench: no extensions; file tree, search, and conflict UI are Octopushy's to build [E6] |
| **B. VS Code for the Web, self-built** | The open-source VS Code web build plus an Octopushy extension implementing a `FileSystemProvider` over the draft API | Closest to github.dev; no server-side shell; github1s proves the approach | Maintaining a VS Code build and extension; the Microsoft Marketplace cannot be used, only Open VSX; one report of remote file systems failing in locally built web versions [E4], [E5], [E7] |
| **C. code-server / OpenVSCode Server** | A full VS Code server process with a checkout on disk | Complete IDE, including extensions and terminal | A separate Node.js service, not self-contained; a terminal is a shell on its host, so it needs its own isolated machine without Octopushy secrets or Git push rights; OpenVSCode Server is unauthenticated unless a connection token is set; about 1 GB RAM and 2 vCPUs [E2], [E3] |
| **D. Open in local VS Code** | Nothing in Octopushy; a link opens the repository in the user's own editor | Free | Bypasses drafts; changes arrive as ordinary commits and are only seen by change detection |

## Recommendation

1. Build **A** as the first editing surface. Drafts need an editor anyway, and
   Monaco fits the self-contained preference.
2. Design the draft API so that **B** can attach later as a github.dev-like view
   over the same drafts, without changing how drafts are validated or committed.
3. Offer **D** as a link. Treat **C** as an optional, isolated add-on only if a
   terminal in the browser becomes a real need.

Whichever editor is used, it never pushes to Git itself: publishing a draft goes
through Octopushy's validate → merge → commit → push path.

## Decision

**October 14, 2026:** the user chose **A, embedded Monaco**. It edits any file in
the repository, like a normal editor. Options B and C are not planned.

## Sources

- **[E1] GitHub — The github.dev web-based editor:**
  https://docs.github.com/en/codespaces/the-githubdev-web-based-editor
- **[E2] Coder — code-server:** https://github.com/coder/code-server
- **[E3] Gitpod — OpenVSCode Server:** https://github.com/gitpod-io/openvscode-server
- **[E4] vscode-web (web build of VS Code, bring your own FileSystemProvider):**
  https://github.com/0xGG/vscode-web/blob/main/README.md
- **[E5] github1s (VS Code web build browsing GitHub through an API-backed
  file system):** https://github.com/conwnet/github1s
- **[E6] Microsoft — Monaco Editor:** https://github.com/microsoft/monaco-editor
- **[E7] Report of a remote file system failing in a locally built web version:**
  https://github.com/microsoft/vscode-test-web/issues/22
