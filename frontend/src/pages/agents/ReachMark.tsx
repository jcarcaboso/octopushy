import { Check, Minus, X } from "lucide-react";
import type { Mark } from "./reach";

const words: Record<Mark, string> = { yes: "Reaches", no: "Does not reach", unknown: "Not known yet:" };

/** A check, a cross or a dash, with the same in words for screen readers, so colour is never the only sign. */
export function ReachMark({ mark }: { mark: Mark }) {
  const Icon = mark === "yes" ? Check : mark === "no" ? X : Minus;
  return (
    <>
      <Icon className={`reach-mark reach-mark--${mark}`} size={13} strokeWidth={2.6} aria-hidden="true" />
      <span className="visually-hidden">{words[mark]} </span>
    </>
  );
}
