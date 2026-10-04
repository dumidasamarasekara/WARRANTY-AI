import { RouterProvider } from 'react-router'
import { setStaffAccessTokenProvider } from '../shared/api/client'
import { ToastProvider } from '../shared/ui'
import { isTenantChannelHost } from './channel'
import { staffAccessToken } from './oidc'
import { QueryProvider } from './QueryProvider'
import { createAppRouter } from './routes'

setStaffAccessTokenProvider(staffAccessToken)

const router = createAppRouter(isTenantChannelHost(window.location.hostname))

export function App() {
  return (
    <QueryProvider>
      <ToastProvider>
        <RouterProvider router={router} />
      </ToastProvider>
    </QueryProvider>
  )
}
