import { Callout } from "../../components/Callout";

/** The server's reason for a 409: another version became active meanwhile. */
export function ConflictNote({ message, onRefresh }: { message: string; onRefresh: () => void }) {
  return (
    <Callout
      kind="warning"
      title={message}
      alert
      actions={
        <button type="button" className="button button--small" onClick={onRefresh}>
          Refresh
        </button>
      }
    >
      Refresh to see the version that is active now.
    </Callout>
  );
}
