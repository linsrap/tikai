import { problemMessage } from '../auth/auth'

/** The data of an API call, or an Error with the problem's message. */
export async function unwrap<T>(
  call: Promise<{ data?: T; error?: unknown; response: Response }>,
  what: string,
): Promise<T> {
  const { data, error, response } = await call
  if (error !== undefined || !response.ok) {
    throw new Error(problemMessage(error, `Could not ${what} (${response.status})`))
  }
  return data as T
}
