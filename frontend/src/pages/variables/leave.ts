import { createContext, useContext } from "react";

/** Runs a step that leaves the open entry, after asking first when it holds edits that are not saved. */
export type Leave = (step: () => void) => void;

export const LeaveContext = createContext<Leave>((step) => step());

export function useLeave(): Leave {
  return useContext(LeaveContext);
}
