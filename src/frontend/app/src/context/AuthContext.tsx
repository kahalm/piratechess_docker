import { createContext, useContext, useState, useEffect, ReactNode } from 'react'

interface AuthState {
  token: string | null
  username: string | null
}

interface AuthContextType extends AuthState {
  login: (token: string, username: string) => void
  logout: () => void
  isAuthenticated: boolean
}

const AuthContext = createContext<AuthContextType | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [auth, setAuth] = useState<AuthState>(() => ({
    token: localStorage.getItem('token'),
    username: localStorage.getItem('username'),
  }))

  useEffect(() => {
    if (auth.token) {
      localStorage.setItem('token', auth.token)
      localStorage.setItem('username', auth.username ?? '')
    } else {
      localStorage.removeItem('token')
      localStorage.removeItem('username')
    }
  }, [auth])

  const login = (token: string, username: string) =>
    setAuth({ token, username })

  const logout = () => setAuth({ token: null, username: null })

  return (
    <AuthContext.Provider value={{ ...auth, login, logout, isAuthenticated: !!auth.token }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
