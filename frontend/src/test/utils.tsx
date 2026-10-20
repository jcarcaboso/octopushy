import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import type { ReactElement } from "react";
import { MemoryRouter, Route, Routes } from "react-router";
import { vi } from "vitest";

export interface RecordedRequest {
  method: string;
  path: string;
  search: string;
  body: unknown;
}

type Handler = (request: RecordedRequest) => unknown;

/**
 * Replaces fetch with handlers keyed by "METHOD /path". A handler returns a JSON body,
 * or a Response for anything else. Every request is recorded.
 */
export function mockApi(handlers: Record<string, Handler>) {
  const requests: RecordedRequest[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const request = input instanceof Request ? input : new Request(input);
    const url = new URL(request.url);
    const text = request.method === "GET" ? "" : await request.text();
    const recorded: RecordedRequest = {
      method: request.method,
      path: url.pathname,
      search: url.search,
      body: text ? JSON.parse(text) : undefined,
    };
    requests.push(recorded);
    const handler = handlers[`${request.method} ${url.pathname}`];
    if (!handler) {
      return new Response(JSON.stringify({ title: `No mock for ${request.method} ${url.pathname}` }), {
        status: 404,
        headers: { "content-type": "application/problem+json" },
      });
    }
    const result = handler(recorded);
    if (result instanceof Response) return result;
    if (result === undefined) return new Response(null, { status: 204 });
    return new Response(JSON.stringify(result), { status: 200, headers: { "content-type": "application/json" } });
  });
  vi.stubGlobal("fetch", fetchMock);
  return { requests, fetchMock };
}

export function problem(status: number, title: string) {
  return new Response(JSON.stringify({ status, title }), {
    status,
    headers: { "content-type": "application/problem+json" },
  });
}

export function renderAt(ui: ReactElement, { path, route }: { path: string; route: string }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[route]}>
        <Routes>
          <Route path={path} element={ui} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}
