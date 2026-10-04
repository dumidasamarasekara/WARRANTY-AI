import { RouterProvider } from 'react-router'
import { ToastProvider } from '../shared/ui'
import { isTenantChannelHost } from './channel'
import { QueryProvider } from './QueryProvider'
import { createAppRouter } from './routes'

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
