import { useState, type FormEvent } from "react";
import { Navigate, useNavigate } from "react-router";
import { useAuthState, useSetup } from "../api/queries";
import { useDocumentTitle } from "../components/useDocumentTitle";
import { ErrorMessage, Spinner } from "../components/States";
import { AuthFrame } from "./AuthFrame";

const minPassword = 12;

export function SetupPage() {
  useDocumentTitle("Create the account");
  const { data: auth, isPending } = useAuthState();
  const setup = useSetup();
  const navigate = useNavigate();
  const [setupCode, setSetupCode] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [repeat, setRepeat] = useState("");
  const [touched, setTouched] = useState(false);

  if (isPending) {
    return (
      <div className="center-screen">
        <Spinner />
      </div>
    );
  }
  if (auth && !auth.setupRequired) return <Navigate to={auth.authenticated ? "/" : "/login"} replace />;

  const tooShort = password.length > 0 && password.length < minPassword;
  const mismatch = repeat.length > 0 && repeat !== password;

  function submit(event: FormEvent) {
    event.preventDefault();
    setTouched(true);
    if (password.length < minPassword || password !== repeat) return;
    setup.mutate(
      { setupCode: setupCode.trim(), username: username.trim(), password },
      { onSuccess: () => navigate("/", { replace: true }) },
    );
  }

  return (
    <AuthFrame
      title="Create the account"
      intro={
        <p>
          This is a fresh install. Enter the one-time setup code to create the single account. The server wrote the
          code to the file <code className="mono">setup-code</code> in its data directory when it started, so only
          someone with access to the server can claim it.
        </p>
      }
    >
      <form className="form" onSubmit={submit} noValidate>
        <div className="field">
          <label htmlFor="setup-code">Setup code</label>
          <input
            id="setup-code"
            className="mono"
            value={setupCode}
            onChange={(e) => setSetupCode(e.target.value)}
            autoComplete="one-time-code"
            spellCheck={false}
            autoCapitalize="characters"
            required
            autoFocus
          />
        </div>
        <div className="field">
          <label htmlFor="setup-user">User name</label>
          <input
            id="setup-user"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoComplete="username"
            maxLength={64}
            required
          />
        </div>
        <div className="field">
          <label htmlFor="setup-password">Password</label>
          <input
            id="setup-password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="new-password"
            aria-describedby="setup-password-hint"
            aria-invalid={(touched || tooShort) && password.length < minPassword ? true : undefined}
            required
          />
          <div id="setup-password-hint" className={tooShort ? "field__error" : "field__hint"}>
            At least {minPassword} characters{password.length > 0 ? ` (${password.length} so far)` : ""}.
          </div>
        </div>
        <div className="field">
          <label htmlFor="setup-repeat">Repeat the password</label>
          <input
            id="setup-repeat"
            type="password"
            value={repeat}
            onChange={(e) => setRepeat(e.target.value)}
            autoComplete="new-password"
            aria-invalid={mismatch ? true : undefined}
            aria-describedby={mismatch ? "setup-repeat-error" : undefined}
            required
          />
          {mismatch ? (
            <div id="setup-repeat-error" className="field__error">
              The passwords differ.
            </div>
          ) : null}
        </div>
        <ErrorMessage error={setup.error} />
        <button
          type="submit"
          className="button button--primary button--block"
          disabled={setup.isPending || !setupCode.trim() || !username.trim()}
        >
          {setup.isPending ? "Creating…" : "Create the account"}
        </button>
      </form>
    </AuthFrame>
  );
}
