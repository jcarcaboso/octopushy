import { QueryClientProvider } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { BrowserRouter, Route, Routes } from "react-router";
import { setUnauthorizedHandler } from "./api/client";
import { createQueryClient } from "./api/queryClient";
import { keys, signedOut } from "./api/queries";
import { Layout } from "./components/Layout";
import { RequireAuth } from "./components/RequireAuth";
import { AgentsPage } from "./pages/AgentsPage";
import { GuestPage } from "./pages/GuestPage";
import { InstallationPage } from "./pages/InstallationPage";
import { InventoryPage } from "./pages/InventoryPage";
import { LoginPage } from "./pages/LoginPage";
import { NotFoundPage } from "./pages/NotFoundPage";
import { OverviewPage } from "./pages/OverviewPage";
import { RepositoryPage } from "./pages/RepositoryPage";
import { RunPage } from "./pages/RunPage";
import { RunsPage } from "./pages/RunsPage";
import { SetupPage } from "./pages/SetupPage";
import { VariablesPage } from "./pages/VariablesPage";
import { WorkerPreparationPage } from "./pages/WorkerPreparationPage";

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/setup" element={<SetupPage />} />
      <Route path="/login" element={<LoginPage />} />
      <Route
        element={
          <RequireAuth>
            <Layout />
          </RequireAuth>
        }
      >
        <Route index element={<OverviewPage />} />
        <Route path="inventory" element={<InventoryPage />} />
        <Route path="guests/:id" element={<GuestPage />} />
        <Route path="installations/:id" element={<InstallationPage />} />
        <Route path="runs" element={<RunsPage />} />
        <Route path="runs/:id" element={<RunPage />} />
        <Route path="agents" element={<AgentsPage />} />
        <Route path="agents/prepare" element={<WorkerPreparationPage />} />
        <Route path="variables" element={<VariablesPage />} />
        <Route path="repository" element={<RepositoryPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}

export function App() {
  const [client] = useState(createQueryClient);

  useEffect(() => {
    // A 401 from any call means the session ended: forget the data and sign in again.
    setUnauthorizedHandler(() => {
      void client.cancelQueries();
      client.removeQueries({ predicate: (q) => q.queryKey[0] !== keys.auth[0] });
      client.setQueryData(keys.auth, signedOut);
    });
    return () => setUnauthorizedHandler(null);
  }, [client]);

  return (
    <QueryClientProvider client={client}>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </QueryClientProvider>
  );
}
