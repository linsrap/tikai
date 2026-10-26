import { emptyProviderForm, toCreateRequest, toUpdateRequest } from './llm'

describe('provider requests', () => {
  const form = { ...emptyProviderForm, apiKey: '  sk-ant-api03-key  ' }

  it('creates an Anthropic provider with the suggested model and the trimmed key', () => {
    expect(toCreateRequest(form)).toEqual({
      name: 'Anthropic',
      defaultModel: 'claude-opus-5-5',
      apiKey: 'sk-ant-api03-key',
      kind: 'anthropic',
      baseUrl: null,
    })
  })

  it('sends a base URL only when one is entered', () => {
    expect(toCreateRequest({ ...form, baseUrl: ' https://gw.example.org ' }).baseUrl).toBe(
      'https://gw.example.org',
    )
  })

  it('keeps the stored key when the key field is left empty', () => {
    expect(toUpdateRequest({ ...form, apiKey: '   ' }).apiKey).toBeNull()
    expect(toUpdateRequest(form).apiKey).toBe('sk-ant-api03-key')
  })
})
