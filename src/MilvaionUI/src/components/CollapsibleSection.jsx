import Icon from './Icon'
import { useLocalStorageState } from '../hooks/useLocalStorageState'
import './CollapsibleSection.css'

/* eslint-disable react/prop-types */

/**
 * Başlığından katlanabilen bölüm.
 *
 * Açık/kapalı tercihi <c>storageKey</c> altında localStorage'da tutuluyor, yani her
 * bölüm kendi durumunu ayrı hatırlıyor. Sayfayı her açtığında aynı bölümü kapatmak
 * zorunda kalmak, katlamayı işe yaramaz hale getiriyor.
 *
 * @param {string} storageKey Tercihin saklanacağı anahtar.
 * @param {React.ReactNode} title Başlık içeriği.
 * @param {string} icon Başlığın solundaki ikon adı.
 * @param {React.ReactNode} actions Başlık çubuğundaki düğmeler. Tıklamaları katlamayı tetiklemiyor.
 * @param {boolean} defaultOpen Kayıtlı tercih yokken açık mı gelsin.
 */
function CollapsibleSection({
  storageKey,
  title,
  icon,
  actions = null,
  defaultOpen = true,
  className = '',
  children,
}) {
  const [open, setOpen] = useLocalStorageState(storageKey, defaultOpen)

  const toggle = () => setOpen(o => !o)

  return (
    <div className={`collapsible-section${open ? ' is-open' : ''} ${className}`.trim()}>
      {/* Çubuğun tamamı tıklanabilir. Klavye erişimi sağdaki düğmeden geliyor;
          çubuğu da düğme yapmak, içindeki eylem düğmelerini geçersiz hale getirirdi. */}
      <div className="collapsible-head" onClick={toggle}>
        <h2>
          {icon && <Icon name={icon} size={22} />}
          {title}
        </h2>

        {actions && (
          <div className="collapsible-actions" onClick={e => e.stopPropagation()}>
            {actions}
          </div>
        )}

        <button
          type="button"
          className="collapsible-chevron"
          onClick={e => { e.stopPropagation(); toggle() }}
          aria-expanded={open}
          aria-label={open ? 'Collapse section' : 'Expand section'}
        >
          <Icon name="expand_more" size={20} />
        </button>
      </div>

      {/* Kapalıyken de DOM'da duruyor: yüksekliği animasyonla değiştirmek için
          tarayıcının içeriği ölçebilmesi gerekiyor. Görünürlük CSS'te kapatılıyor,
          böylece kapalı bölüm sekmeyle gezilemiyor ve ekran okuyucuya okunmuyor. */}
      <div className="collapsible-outer" aria-hidden={!open}>
        <div className="collapsible-inner">
          <div className="collapsible-body">{children}</div>
        </div>
      </div>
    </div>
  )
}

export default CollapsibleSection
