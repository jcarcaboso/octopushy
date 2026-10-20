import { QueryClient } from "@tanstack/react-query";
import { ApiError } from "./client";

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Problems the server meant (4xx) are not retried; network blips are, twice.
        retry: (count, error) => !(error instanceof ApiError && error.status < 500) && count < 2,
        refetchOnWindowFocus: true,
        staleTime: 2_000,
      },
      mutations: { retry: false },
    },
  });
}
