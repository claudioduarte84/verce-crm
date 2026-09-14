import { Route, Routes } from 'react-router-dom'
import { RequireAuth } from '../auth/RequireAuth'
import { HomePage } from '../pages/HomePage'
import { LoginPage } from '../pages/LoginPage'
import { NotFoundPage } from '../pages/NotFoundPage'
import { SetupAccountPage } from '../pages/SetupAccountPage'
import { CustomersPage } from '../pages/CustomersPage'
import { SettingsPage } from '../pages/SettingsPage'

/** Routing foundation (mission §14). Only the routes S1 needs: login, setup-account, an
 * authenticated home placeholder, and not-found. Customers/Catalog/Quoting/... are S2+. */
export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/setup-account" element={<SetupAccountPage />} />
      <Route
        path="/"
        element={
          <RequireAuth>
            <HomePage />
          </RequireAuth>
        }
      />
      <Route path="/customers" element={<RequireAuth><CustomersPage /></RequireAuth>} />
      <Route path="/settings" element={<RequireAuth><SettingsPage /></RequireAuth>} />
      <Route path="*" element={<NotFoundPage />} />
    </Routes>
  )
}
