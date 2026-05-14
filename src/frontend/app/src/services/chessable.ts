import { api } from './api'

export interface CredentialResponse {
  id: number
  useBearer: boolean
  hasCredentials: boolean
  maskedBearer: string | null
  maskedEmail: string | null
  maskedPassword: string | null
}

export interface CourseListItem {
  bid: string
  name: string
}

export const chessableService = {
  getCredentials: () =>
    api.get<CredentialResponse>('/chessable/credentials'),

  saveCredentials: (data: {
    useBearer: boolean
    bearer?: string
    email?: string
    password?: string
  }) => api.post<CredentialResponse>('/chessable/credentials', data),

  deleteCredentials: () =>
    api.del<void>('/chessable/credentials'),

  testCredentials: () =>
    api.post<{ message: string }>('/chessable/test', {}),

  getCourses: () =>
    api.get<CourseListItem[]>('/chessable/courses'),
}
