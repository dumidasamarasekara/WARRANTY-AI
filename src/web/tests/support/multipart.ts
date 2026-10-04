import { vi } from 'vitest'

interface NodeBufferModule {
  Blob: typeof Blob
  File: typeof File
}

/**
 * Node's fetch (used by MSW and openapi-fetch under Vitest) cannot read a multipart body built from
 * jsdom's Blob, File and FormData: the request body never ends. Tests that send or read multipart
 * bodies swap in Node's own classes, which behave like a browser's. Call in `beforeEach`; undo with
 * `vi.unstubAllGlobals()`.
 */
export async function useNodeMultipartClasses(): Promise<void> {
  // A non-literal specifier keeps Node's types out of the browser type-check of the tests.
  const specifier: string = 'node:buffer'
  const { Blob: NodeBlob, File: NodeFile } = (await import(/* @vite-ignore */ specifier)) as NodeBufferModule
  const NodeFormData = (await new Response(new URLSearchParams('probe=1')).formData()).constructor
  vi.stubGlobal('Blob', NodeBlob)
  vi.stubGlobal('File', NodeFile)
  vi.stubGlobal('FormData', NodeFormData)
}
