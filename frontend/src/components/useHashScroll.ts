import { useEffect } from "react";
import { useLocation } from "react-router";

/** Scrolls to the element named by the URL's #fragment once the page has content. */
export function useHashScroll(ready: boolean) {
  const { hash } = useLocation();
  useEffect(() => {
    if (!ready || !hash) return;
    const el = document.getElementById(decodeURIComponent(hash.slice(1)));
    el?.scrollIntoView({ block: "start" });
  }, [ready, hash]);
}
