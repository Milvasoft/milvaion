import { useState, useCallback } from 'react'

/**
 * useState gibi çalışır, ama değeri localStorage'da da tutar.
 *
 * Depolama erişimi try/catch içinde: gizli sekmelerde ve bazı kurumsal tarayıcı
 * politikalarında localStorage'a yazmak hata fırlatıyor. Bir tercihi
 * hatırlayamamak, sayfayı çökertmeye değecek bir sorun değil.
 *
 * @param {string} key Depolama anahtarı. Çakışmaması için alan adıyla başlaması iyi olur.
 * @param {*} defaultValue Kayıt yoksa ya da okunamıyorsa kullanılacak değer.
 */
export function useLocalStorageState(key, defaultValue) {
  const [value, setValue] = useState(() => {
    try {
      const stored = window.localStorage.getItem(key)

      return stored === null ? defaultValue : JSON.parse(stored)
    } catch {
      return defaultValue
    }
  })

  const set = useCallback((next) => {
    setValue(prev => {
      // Fonksiyon biçimi destekleniyor, useState ile aynı sözleşme.
      const resolved = typeof next === 'function' ? next(prev) : next

      try {
        window.localStorage.setItem(key, JSON.stringify(resolved))
      } catch {
        // Depolama kullanılamıyor; değer yine de bu oturumda geçerli.
      }

      return resolved
    })
  }, [key])

  return [value, set]
}

export default useLocalStorageState
