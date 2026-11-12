import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { CopyButton } from "./CopyButton";
import { Dialog } from "./Dialog";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** Plain HTTP: no clipboard API, so the button falls back to a selected textarea and execCommand. */
function plainHttp() {
  vi.stubGlobal("isSecureContext", false);
  const copied: { text: string; inDialog: boolean }[] = [];
  document.execCommand = vi.fn(() => {
    const area = document.querySelector("textarea")!;
    copied.push({ text: area.value, inDialog: area.closest("dialog[open]") !== null });
    return true;
  });
  return copied;
}

it("copies from inside an open modal dialog, where the rest of the page is inert", async () => {
  const copied = plainHttp();
  render(
    <Dialog open onClose={() => {}} title="Enroll">
      <CopyButton text="octo_enroll_secret" label="Copy token" />
    </Dialog>,
  );
  const button = screen.getByRole("button", { name: "Copy token" });
  await userEvent.click(button);
  expect(copied).toEqual([{ text: "octo_enroll_secret", inDialog: true }]);
  expect(await screen.findByRole("button", { name: /Copied/ })).toHaveFocus();
  expect(screen.getByText("Copied to the clipboard")).toBeInTheDocument();
  expect(document.querySelector("textarea")).toBeNull();
});

it("copies from the page when there is no dialog", async () => {
  const copied = plainHttp();
  render(<CopyButton text="plain" label="Copy" />);
  await userEvent.click(screen.getByRole("button", { name: "Copy" }));
  expect(copied).toEqual([{ text: "plain", inDialog: false }]);
});

it("says so when the copy fails", async () => {
  plainHttp();
  document.execCommand = vi.fn(() => false);
  render(<CopyButton text="plain" label="Copy" />);
  await userEvent.click(screen.getByRole("button", { name: "Copy" }));
  expect(await screen.findByRole("button", { name: "Copy failed" })).toBeVisible();
});
