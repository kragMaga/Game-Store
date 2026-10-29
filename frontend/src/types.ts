// These contracts mirror API DTOs. Business roles describe live memberships,
// never global account permissions. The API remains the authority for each action.
export interface Page<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalItems: number;
  totalPages: number;
}
export interface Business {
  id: string;
  name: string;
  currency: string;
  role: 'Owner' | 'Admin' | 'Staff' | 'Viewer';
  closedAtUtc: string | null;
}
export interface Master {
  id: string;
  name: string;
  isActive: boolean;
}
export interface Product {
  id: string;
  name: string;
  sku: string;
  categoryId: string | null;
  stockUnit: string;
  stockUnitId: string;
  allowsFractionalStock: boolean;
  quantityOnHand: number;
  reorderLevel: number;
  isLowStock: boolean;
  openingStockRecorded: boolean;
  isActive: boolean;
  defaultSellingPrice: number | null;
}
export interface ProductUnit {
  id: string;
  name: string;
  conversionFactor: number;
  allowsFractionalQuantity: boolean;
  isBaseUnit: boolean;
  unitOfMeasureId: string;
}
export interface Dashboard {
  localDate: string;
  currency: string;
  currencyDecimalPlaces: number;
  salesToday: number;
  returnsToday: number;
  netSalesToday: number;
  customerDebt: number;
  lowStockCount: number;
  failedNotifications: number;
  debtReminder: { customerCount: number; amount: number };
  lowStock: {
    id: string;
    name: string;
    sku: string;
    stockUnit: string;
    quantity: number;
    reorderLevel: number;
  }[];
  recentActivity: { id: string; action: string; actorUserId: number; createdAtUtc: string }[];
}
export const isAdmin = (business: Business) =>
  business.role === 'Owner' || business.role === 'Admin';
export const quantity = (value: number) =>
  new Intl.NumberFormat(undefined, { maximumFractionDigits: 6 }).format(value);
export const money = (value: number, currency: string, places = 2) =>
  new Intl.NumberFormat(undefined, {
    style: 'currency',
    currency,
    minimumFractionDigits: places,
    maximumFractionDigits: places,
  }).format(value);
