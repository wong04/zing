import { AngleMode } from '@shared/math/expression'

export const angleOptions = Object.values(AngleMode).map((value) => ({ value, label: value }))
