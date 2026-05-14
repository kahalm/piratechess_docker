import { useState, useEffect, FormEvent } from 'react'
import { chessableService, CredentialResponse } from '../services/chessable'

type Tab = 'bearer' | 'email'

export default function CredentialsPage() {
  const [tab, setTab] = useState<Tab>('bearer')
  const [bearer, setBearer] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [status, setStatus] = useState<CredentialResponse | null>(null)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(false)
  const [testing, setTesting] = useState(false)

  useEffect(() => {
    chessableService.getCredentials().then(setStatus).catch(() => {})
  }, [])

  const handleSave = async (e: FormEvent) => {
    e.preventDefault()
    setError('')
    setMessage('')
    setLoading(true)
    try {
      const res = await chessableService.saveCredentials(
        tab === 'bearer'
          ? { useBearer: true, bearer }
          : { useBearer: false, email, password },
      )
      setStatus(res)
      setMessage('Credentials saved')
      setBearer('')
      setPassword('')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed')
    } finally {
      setLoading(false)
    }
  }

  const handleTest = async () => {
    setError('')
    setMessage('')
    setTesting(true)
    try {
      const res = await chessableService.testCredentials()
      setMessage(res.message)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Test failed')
    } finally {
      setTesting(false)
    }
  }

  const handleDelete = async () => {
    setError('')
    setMessage('')
    try {
      await chessableService.deleteCredentials()
      setStatus(null)
      setMessage('Credentials deleted')
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Delete failed')
    }
  }

  const tabStyle = (active: boolean): React.CSSProperties => ({
    padding: '0.5rem 1rem',
    cursor: 'pointer',
    borderBottom: active ? '2px solid #333' : '2px solid transparent',
    background: 'none',
    border: 'none',
    borderBottomWidth: 2,
    borderBottomStyle: 'solid',
    borderBottomColor: active ? '#333' : 'transparent',
    fontWeight: active ? 'bold' : 'normal',
  })

  return (
    <div>
      <h2>Chessable Credentials</h2>

      {status?.hasCredentials && (
        <div style={{ background: '#f0f8f0', border: '1px solid #ccc', borderRadius: 4, padding: '0.75rem', marginBottom: '1rem' }}>
          <p style={{ color: 'green', margin: '0 0 0.5rem' }}>
            Credentials saved ({status.useBearer ? 'Bearer Token' : 'Email + Password'})
          </p>
          {status.useBearer && status.maskedBearer && (
            <p style={{ margin: 0, fontFamily: 'monospace', fontSize: '0.85rem' }}>
              Token: {status.maskedBearer}
            </p>
          )}
          {!status.useBearer && status.maskedEmail && (
            <>
              <p style={{ margin: '0 0 0.25rem', fontFamily: 'monospace', fontSize: '0.85rem' }}>
                Email: {status.maskedEmail}
              </p>
              <p style={{ margin: 0, fontFamily: 'monospace', fontSize: '0.85rem' }}>
                Password: {status.maskedPassword}
              </p>
            </>
          )}
        </div>
      )}
      {status && !status.hasCredentials && (
        <p style={{ color: '#888' }}>No credentials saved</p>
      )}

      <div style={{ display: 'flex', gap: '0.5rem', marginBottom: '1rem', borderBottom: '1px solid #ccc' }}>
        <button style={tabStyle(tab === 'bearer')} onClick={() => setTab('bearer')}>
          Bearer Token
        </button>
        <button style={tabStyle(tab === 'email')} onClick={() => setTab('email')}>
          Email + Password
        </button>
      </div>

      <form onSubmit={handleSave} style={{ maxWidth: 400 }}>
        {tab === 'bearer' ? (
          <div style={{ marginBottom: '1rem' }}>
            <label htmlFor="bearer">Bearer Token</label>
            <textarea
              id="bearer"
              value={bearer}
              onChange={e => setBearer(e.target.value)}
              required
              rows={3}
              style={{ display: 'block', width: '100%' }}
            />
          </div>
        ) : (
          <>
            <div style={{ marginBottom: '1rem' }}>
              <label htmlFor="email">Email</label>
              <input
                id="email"
                type="email"
                value={email}
                onChange={e => setEmail(e.target.value)}
                required
                style={{ display: 'block', width: '100%' }}
              />
            </div>
            <div style={{ marginBottom: '1rem' }}>
              <label htmlFor="password">Password</label>
              <input
                id="password"
                type="password"
                value={password}
                onChange={e => setPassword(e.target.value)}
                required
                style={{ display: 'block', width: '100%' }}
              />
            </div>
          </>
        )}

        {error && <p style={{ color: 'red' }}>{error}</p>}
        {message && <p style={{ color: 'green' }}>{message}</p>}

        <div style={{ display: 'flex', gap: '0.5rem' }}>
          <button type="submit" disabled={loading}>
            {loading ? 'Saving...' : 'Save'}
          </button>
          {status?.hasCredentials && (
            <>
              <button type="button" onClick={handleTest} disabled={testing}>
                {testing ? 'Testing...' : 'Test'}
              </button>
              <button type="button" onClick={handleDelete} style={{ color: 'red' }}>
                Delete
              </button>
            </>
          )}
        </div>
      </form>
    </div>
  )
}
