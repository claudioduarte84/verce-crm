import { useEffect, useState, type FormEvent } from 'react'
import { apiClient } from '../api/client'
import type { AppSettingResponse, BrandActivationRequest, BrandAssetIdResponse, BrandAssetResponse, CompanyProfileRequest, CompanyProfileResponse } from '../api/types'

type ProfileForm = Omit<CompanyProfileRequest, 'version'>

function toProfileForm(profile: CompanyProfileResponse): ProfileForm {
  return {
    legalName: profile.legalName, tradeName: profile.tradeName, document: profile.document, email: profile.email, phone: profile.phone,
    website: profile.website, instagram: profile.instagram, whatsApp: profile.whatsApp, zipCode: profile.zipCode, street: profile.street,
    number: profile.number, complement: profile.complement, district: profile.district, city: profile.city, state: profile.state,
    country: profile.country, timezone: profile.timezone, currency: profile.currency,
  }
}

export function SettingsPage() {
  const [profileVersion, setProfileVersion] = useState<number | string>(0)
  const [profile, setProfile] = useState<ProfileForm | null>(null)
  const [settings, setSettings] = useState<AppSettingResponse[]>([])
  const [assets, setAssets] = useState<BrandAssetResponse[]>([])
  const [message, setMessage] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [assetName, setAssetName] = useState('')
  const [assetType, setAssetType] = useState('PRIMARY_LOGO')

  async function load() {
    const [p, s, a] = await Promise.all([
      apiClient.get<CompanyProfileResponse>('/api/settings/company-profile'),
      apiClient.get<AppSettingResponse[]>('/api/settings'),
      apiClient.get<BrandAssetResponse[]>('/api/settings/brand-assets'),
    ])
    if (!p.ok) { setMessage(p.error.safeMessage); return }
    setProfile(toProfileForm(p.data))
    setProfileVersion(p.data.version)
    if (s.ok) setSettings(s.data)
    if (a.ok) setAssets(a.data)
  }

  // Initial settings are an external-server synchronization, not derived local state.
  // oxlint-disable-next-line react/set-state-in-effect, react-hooks/exhaustive-deps
  useEffect(() => { void load() }, [])

  async function saveProfile(event: FormEvent) {
    event.preventDefault()
    if (!profile) return
    const body: CompanyProfileRequest = { ...profile, version: profileVersion }
    const result = await apiClient.put<void>('/api/settings/company-profile', body)
    setMessage(result.ok ? 'Dados da empresa salvos.' : result.error.safeMessage)
    if (result.ok) await load()
  }

  async function updateSetting(setting: AppSettingResponse) {
    const value = window.prompt(setting.description, setting.value)
    if (value === null) return
    const result = await apiClient.put<void>(`/api/settings/${encodeURIComponent(setting.key)}`, { value, version: setting.version })
    setMessage(result.ok ? 'Configuração salva.' : result.error.safeMessage)
    if (result.ok) await load()
  }

  async function createAsset(event: FormEvent) {
    event.preventDefault()
    const created = await apiClient.post<BrandAssetIdResponse>('/api/settings/brand-assets', { brandAssetTypeCode: assetType, name: assetName })
    if (!created.ok) { setMessage(created.error.safeMessage); return }
    if (file) {
      const form = new FormData()
      form.append('file', file)
      const uploaded = await apiClient.postForm(`/api/settings/brand-assets/${created.data.id}/versions`, form)
      setMessage(uploaded.ok ? 'Asset de marca enviado e normalizado.' : uploaded.error.safeMessage)
    } else setMessage('Asset criado. Envie uma versão PNG, JPEG ou WebP.')
    setAssetName(''); setFile(null)
    await load()
  }

  async function uploadNewVersion(assetId: string, versionFile: File) {
    const form = new FormData()
    form.append('file', versionFile)
    const result = await apiClient.postForm(`/api/settings/brand-assets/${assetId}/versions`, form)
    setMessage(result.ok ? 'Nova versão enviada com sucesso.' : result.error.safeMessage)
    if (result.ok) await load()
  }

  async function activateVersion(asset: BrandAssetResponse, versionId: string) {
    const body: BrandActivationRequest = { versionId, expectedBrandAssetVersion: asset.version }
    const result = await apiClient.post<void>(`/api/settings/brand-assets/${asset.id}/activate`, body)
    setMessage(result.ok ? 'Versão ativada com sucesso.' : result.error.safeMessage)
    if (result.ok) await load()
  }

  if (!profile) return <section className="product-page"><p role={message ? 'alert' : undefined}>{message || 'Carregando configurações…'}</p></section>

  return (
    <section className="product-page">
      <div className="page-heading">
        <div>
          <h1>Configurações</h1>
          <p>Dados da empresa, parâmetros do sistema e biblioteca de marca.</p>
        </div>
      </div>

      <div className="split-grid">
        <form className="card form-stack" onSubmit={saveProfile}>
          <h2>Dados da empresa</h2>
          <label htmlFor="legal-name">Razão social</label>
          <input id="legal-name" required value={profile.legalName} onChange={(e) => setProfile({ ...profile, legalName: e.target.value })} />
          <label htmlFor="trade-name">Nome fantasia</label>
          <input id="trade-name" required value={profile.tradeName} onChange={(e) => setProfile({ ...profile, tradeName: e.target.value })} />
          <label htmlFor="profile-document">CNPJ</label>
          <input id="profile-document" value={profile.document ?? ''} onChange={(e) => setProfile({ ...profile, document: e.target.value })} />
          <label htmlFor="profile-email">E-mail</label>
          <input id="profile-email" type="email" value={profile.email ?? ''} onChange={(e) => setProfile({ ...profile, email: e.target.value })} />
          <label htmlFor="profile-phone">Telefone</label>
          <input id="profile-phone" value={profile.phone ?? ''} onChange={(e) => setProfile({ ...profile, phone: e.target.value })} />
          <label htmlFor="profile-whatsapp">WhatsApp</label>
          <input id="profile-whatsapp" value={profile.whatsApp ?? ''} onChange={(e) => setProfile({ ...profile, whatsApp: e.target.value })} />
          <label htmlFor="profile-website">Site</label>
          <input id="profile-website" value={profile.website ?? ''} onChange={(e) => setProfile({ ...profile, website: e.target.value })} />
          <label htmlFor="profile-instagram">Instagram</label>
          <input id="profile-instagram" value={profile.instagram ?? ''} onChange={(e) => setProfile({ ...profile, instagram: e.target.value })} />
          <label htmlFor="profile-zip">CEP</label>
          <input id="profile-zip" value={profile.zipCode ?? ''} onChange={(e) => setProfile({ ...profile, zipCode: e.target.value })} />
          <label htmlFor="profile-street">Rua</label>
          <input id="profile-street" value={profile.street ?? ''} onChange={(e) => setProfile({ ...profile, street: e.target.value })} />
          <label htmlFor="profile-number">Número</label>
          <input id="profile-number" value={profile.number ?? ''} onChange={(e) => setProfile({ ...profile, number: e.target.value })} />
          <label htmlFor="profile-complement">Complemento</label>
          <input id="profile-complement" value={profile.complement ?? ''} onChange={(e) => setProfile({ ...profile, complement: e.target.value })} />
          <label htmlFor="profile-district">Bairro</label>
          <input id="profile-district" value={profile.district ?? ''} onChange={(e) => setProfile({ ...profile, district: e.target.value })} />
          <label htmlFor="profile-city">Cidade</label>
          <input id="profile-city" value={profile.city ?? ''} onChange={(e) => setProfile({ ...profile, city: e.target.value })} />
          <label htmlFor="profile-state">UF</label>
          <input id="profile-state" maxLength={2} value={profile.state ?? ''} onChange={(e) => setProfile({ ...profile, state: e.target.value })} />
          <label htmlFor="profile-country">País</label>
          <input id="profile-country" maxLength={2} value={profile.country ?? ''} onChange={(e) => setProfile({ ...profile, country: e.target.value })} />
          <label htmlFor="profile-timezone">Fuso horário</label>
          <input id="profile-timezone" value={profile.timezone ?? ''} onChange={(e) => setProfile({ ...profile, timezone: e.target.value })} />
          <label htmlFor="profile-currency">Moeda</label>
          <input id="profile-currency" maxLength={3} value={profile.currency ?? ''} onChange={(e) => setProfile({ ...profile, currency: e.target.value })} />
          <button className="button-primary">Salvar dados</button>
        </form>

        <div className="card">
          <h2>Parâmetros</h2>
          <ul className="data-list">
            {settings.map((setting) => (
              <li key={setting.key}>
                <strong>{setting.key}</strong>
                <span>{setting.value || '—'}</span>
                <button type="button" onClick={() => void updateSetting(setting)}>Editar</button>
              </li>
            ))}
          </ul>
        </div>
      </div>

      <div className="split-grid">
        <form className="card form-stack" onSubmit={createAsset}>
          <h2>Biblioteca de marca</h2>
          <label htmlFor="asset-name">Nome</label>
          <input id="asset-name" required value={assetName} onChange={(e) => setAssetName(e.target.value)} />
          <label htmlFor="asset-type">Tipo</label>
          <select id="asset-type" value={assetType} onChange={(e) => setAssetType(e.target.value)}>
            <option>PRIMARY_LOGO</option><option>COMPACT_LOGO</option><option>NEGATIVE_LOGO</option>
            <option>SYMBOL</option><option>FAVICON</option><option>DOCUMENT_LOGO</option><option>OTHER</option>
          </select>
          <label htmlFor="asset-file">Versão inicial</label>
          <input id="asset-file" type="file" accept="image/png,image/jpeg,image/webp" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
          <button className="button-primary">Criar asset</button>
        </form>

        <div className="card">
          <h2>Assets existentes</h2>
          <ul className="data-list">
            {assets.map((asset) => (
              <li key={asset.id} className="brand-asset">
                <div>
                  <strong>{asset.name}</strong>
                  <span>{asset.brandAssetTypeCode}</span>
                  {asset.currentVersionId && <img src={`/api/settings/brand-assets/versions/${asset.currentVersionId}/content`} alt={`Prévia de ${asset.name}`} />}
                </div>
                <ul className="data-list" aria-label={`Histórico de versões de ${asset.name}`}>
                  {asset.versions.map((version) => (
                    <li key={version.id}>
                      <span>v{version.versionNumber}{version.isCurrent ? ' (atual)' : ''}</span>
                      {!version.isCurrent && (
                        <button type="button" onClick={() => void activateVersion(asset, version.id)}>Ativar</button>
                      )}
                    </li>
                  ))}
                </ul>
                <label>
                  Enviar nova versão
                  <input
                    type="file"
                    accept="image/png,image/jpeg,image/webp"
                    onChange={(e) => { const uploaded = e.target.files?.[0]; if (uploaded) void uploadNewVersion(asset.id, uploaded); e.target.value = '' }}
                  />
                </label>
              </li>
            ))}
          </ul>
        </div>
      </div>

      {message && <p role="status" className="form-error">{message}</p>}
    </section>
  )
}
