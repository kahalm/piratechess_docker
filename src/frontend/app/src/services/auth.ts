import { api } from './api'

interface AuthResponse {
  token: string
  username: string
}

export const authService = {
  login: (username: string, password: string) =>
    api.post<AuthResponse>('/auth/login', { username, password }),

  register: (username: string, email: string, password: string) =>
    api.post<AuthResponse>('/auth/register', { username, email, password }),
}
