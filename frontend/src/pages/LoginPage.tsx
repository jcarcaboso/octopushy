import { useState, type FormEvent } from "react";
import { Navigate, useNavigate, useSearchParams } from "react-router";
import { useAuthState, useLogin } from "../api/queries";
import { useDocumentTitle } from "../components/useDocumentTitle";
import { safeNext } from "../lib/safeNext";
import { ErrorMessage, Spinner } from "../components/States";
import { AuthFrame } from "./AuthFrame";

export function LoginPage() {
  useDocumentTitle("Sign in");
  const { data: auth, isPending } = useAuthState();
  const login = useLogin();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const next = safeNext(params.get("next"));
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");

  if (isPending) {
    return (
      <div className="center-screen">
        <Spinner />
      </div>
    );
  }
  if (auth?.setupRequired) return <Navigate to="/setup" replace />;
  if (auth?.authenticated) return <Navigate to={next} replace />;

  function submit(event: FormEvent) {
    event.preventDefault();
    login.mutate({ username, password }, { onSuccess: () => navigate(next, { replace: true }) });
  }

  return (
    <AuthFrame title="Sign in">
      <form className="form" onSubmit={submit}>
        <div className="field">
          <label htmlFor="login-user">User name</label>
          <input
            id="login-user"
            autoComplete="username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            required
            autoFocus
          />
        </div>
        <div className="field">
          <label htmlFor="login-password">Password</label>
          <input
            id="login-password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
        </div>
        <ErrorMessage error={login.error} />
        <button type="submit" className="button button--primary button--block" disabled={login.isPending}>
          {login.isPending ? "Signing in…" : "Sign in"}
        </button>
      </form>
    </AuthFrame>
  );
}
