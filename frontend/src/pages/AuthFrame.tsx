import type { ReactNode } from "react";

export function AuthFrame({ title, intro, children }: { title: string; intro?: ReactNode; children: ReactNode }) {
  return (
    <main className="center-screen">
      <div className="auth-card">
        <div className="brand brand--large">
          <img src="/favicon.svg" alt="" width={32} height={32} />
          <span>octopushy</span>
        </div>
        <h1 className="auth-card__title">{title}</h1>
        {intro ? <div className="auth-card__intro">{intro}</div> : null}
        {children}
      </div>
    </main>
  );
}
