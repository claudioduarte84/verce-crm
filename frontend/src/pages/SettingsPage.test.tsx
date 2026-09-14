import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { SettingsPage } from './SettingsPage'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn(), postForm: vi.fn() }))
vi.mock('../api/client', () => ({ apiClient: api }))

const profile = { legalName: 'VERCE 3D', tradeName: 'VERCE', timezone: 'America/Sao_Paulo', currency: 'BRL', version: 1 }
describe('SettingsPage', () => {
  beforeEach(() => {
    api.get.mockReset(); api.post.mockReset(); api.put.mockReset(); api.postForm.mockReset()
    api.get.mockImplementation((path: string) => Promise.resolve(path.includes('company-profile') ? { ok: true, data: profile } : { ok: true, data: [] }))
  })

  it('loads the company profile and saves an edited value through the centralized client', async () => {
    const user = userEvent.setup()
    api.put.mockResolvedValue({ ok: true, data: undefined })
    render(<SettingsPage />)
    const legalName = await screen.findByLabelText('Razão social')
    await user.clear(legalName); await user.type(legalName, 'VERCE 3D Atualizada')
    await user.click(screen.getByRole('button', { name: 'Salvar dados' }))
    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/api/settings/company-profile', expect.objectContaining({ legalName: 'VERCE 3D Atualizada', version: 1 })))
    expect(await screen.findByRole('status')).toHaveTextContent('Dados da empresa salvos.')
  })

  it('renders the server error safely and creates/uploads a first brand version', async () => {
    const user = userEvent.setup()
    api.post.mockResolvedValue({ ok: true, data: { id: 'asset-1' } }); api.postForm.mockResolvedValue({ ok: false, error: { safeMessage: 'Requisição inválida.' } })
    render(<SettingsPage />)
    await screen.findByLabelText('Nome')
    await user.type(screen.getByLabelText('Nome'), 'Marca de teste')
    const file = new File(['tiny'], 'logo.png', { type: 'image/png' })
    await user.upload(screen.getByLabelText('Versão inicial'), file)
    await user.click(screen.getByRole('button', { name: 'Criar asset' }))
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/settings/brand-assets', { brandAssetTypeCode: 'PRIMARY_LOGO', name: 'Marca de teste' }))
    expect(api.postForm).toHaveBeenCalledWith('/api/settings/brand-assets/asset-1/versions', expect.any(FormData))
    expect(await screen.findByRole('status')).toHaveTextContent('Requisição inválida.')
  })

  it('saves every normative company profile field, not just a subset', async () => {
    const user = userEvent.setup()
    api.put.mockResolvedValue({ ok: true, data: undefined })
    render(<SettingsPage />)
    await screen.findByLabelText('Razão social')
    await user.type(screen.getByLabelText('CNPJ'), '73.894.567/0001-22')
    await user.type(screen.getByLabelText('WhatsApp'), '11999998888')
    await user.type(screen.getByLabelText('Site'), 'https://verce3d.example')
    await user.type(screen.getByLabelText('Cidade'), 'São Paulo')
    await user.type(screen.getByLabelText('UF'), 'SP')
    await user.click(screen.getByRole('button', { name: 'Salvar dados' }))
    await waitFor(() => expect(api.put).toHaveBeenCalledWith('/api/settings/company-profile', expect.objectContaining({
      document: '73.894.567/0001-22', whatsApp: '11999998888', website: 'https://verce3d.example', city: 'São Paulo', state: 'SP',
    })))
  })

  it('uploads a new version onto an existing brand asset and activates a historical version', async () => {
    const user = userEvent.setup()
    const asset = {
      id: 'asset-1', name: 'Logo principal', brandAssetTypeCode: 'PRIMARY_LOGO', isActive: true, currentVersionId: 'v2', version: 4,
      versions: [
        { id: 'v1', versionNumber: 1, contentType: 'image/png', fileSizeBytes: 100, widthPx: 10, heightPx: 10, uploadedAt: '2026-01-01T00:00:00Z', isCurrent: false },
        { id: 'v2', versionNumber: 2, contentType: 'image/png', fileSizeBytes: 100, widthPx: 10, heightPx: 10, uploadedAt: '2026-01-02T00:00:00Z', isCurrent: true },
      ],
    }
    api.get.mockImplementation((path: string) => Promise.resolve(
      path.includes('company-profile') ? { ok: true, data: profile } : path.includes('brand-assets') ? { ok: true, data: [asset] } : { ok: true, data: [] },
    ))
    api.postForm.mockResolvedValue({ ok: true, data: { id: 'v3', versionNumber: 3, contentType: 'image/png' } })
    api.post.mockResolvedValue({ ok: true, data: undefined })
    render(<SettingsPage />)
    expect(await screen.findByText('v1')).toBeInTheDocument()
    expect(screen.getByText('v2 (atual)')).toBeInTheDocument()

    const file = new File(['tiny'], 'v3.png', { type: 'image/png' })
    await user.upload(screen.getByLabelText('Enviar nova versão'), file)
    await waitFor(() => expect(api.postForm).toHaveBeenCalledWith('/api/settings/brand-assets/asset-1/versions', expect.any(FormData)))

    await user.click(screen.getByRole('button', { name: 'Ativar' }))
    await waitFor(() => expect(api.post).toHaveBeenCalledWith('/api/settings/brand-assets/asset-1/activate', { versionId: 'v1', expectedBrandAssetVersion: 4 }))
  })
})
