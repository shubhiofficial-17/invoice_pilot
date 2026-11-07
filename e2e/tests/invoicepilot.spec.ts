import { expect, test, type Page } from '@playwright/test';
import { readdirSync } from 'node:fs';
import { basename, join } from 'node:path';

// PDFs come from the evaluation generator:
//   dotnet run --project eval/InvoicePilot.Eval -- --mode engine --pdfs --out e2e/fixtures
const pdfDir = join(__dirname, '..', 'fixtures', 'pdfs');
const PASSWORD = process.env.DEMO_PASSWORD ?? 'invoicepilot-demo';
const CLERK = 'asha.rao@invoicepilot.example';

function fixture(category: string): string {
  const file = readdirSync(pdfDir).find((f) => f.endsWith(`-${category}.pdf`));
  if (!file) throw new Error(`No ${category} PDF in ${pdfDir}. Generate them first.`);
  return join(pdfDir, file);
}

async function signIn(page: Page, email: string): Promise<void> {
  await page.goto('/sign-in');
  await page.getByTestId('email').fill(email);
  await page.getByTestId('password').fill(PASSWORD);
  await page.getByTestId('submit').click();
  await expect(page.getByTestId('user')).toBeVisible();
}

async function signOut(page: Page): Promise<void> {
  await page.getByTestId('sign-out').click();
  await expect(page).toHaveURL(/sign-in/);
}

/** Uploads from the inbox and opens the new invoice; waits until it is ready for a person. */
async function uploadAndOpen(page: Page, category: string): Promise<void> {
  const file = fixture(category);
  await page.goto('/');
  await page.getByTestId('upload-input').setInputFiles(file);
  await page.getByRole('link', { name: basename(file) }).click();
  await expect(page).toHaveURL(/\/invoices\/ap_/);
  await expect(page.getByTestId('status')).toHaveText(/Awaiting review/);
}

test('signed-out visitors are sent to sign in', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/sign-in\?returnUrl=/);
});

test('a clean invoice is read, investigated, confirmed and booked', async ({ page }) => {
  await signIn(page, CLERK);
  await uploadAndOpen(page, 'clean');

  await expect(page.getByTestId('outcome')).toHaveText('Approve');
  await expect(page.getByTestId('findings')).toBeVisible();
  await page.getByTestId('confirm').click();

  await expect(page.getByTestId('status')).toHaveText('Approved');
  await expect(page.getByTestId('booking')).toContainText('Booked in the ERP');
  await expect(page.getByTestId('chain-ok')).toBeVisible();
  await expect(page.getByTestId('audit')).toContainText('Booked in the ERP');
});

test('an invoice that tries to talk its way to approval is put on hold', async ({ page }) => {
  await signIn(page, CLERK);
  await uploadAndOpen(page, 'prompt_injection');

  await expect(page.getByTestId('outcome')).toHaveText('Hold');
  await expect(page.getByTestId('findings')).toContainText('received');
  // The policy won't let a reviewer loosen a hold.
  await page.getByTestId('toggle-override').click();
  await expect(page.locator('[data-testid=override-outcome] option[value=approve]')).toBeDisabled();

  await page.getByTestId('confirm').click();
  await expect(page.getByTestId('status')).toHaveText('On hold');
  await expect(page.getByTestId('outbox')).toContainText('ar@acme-industrial.example');
});

test('a price increase goes to the buyer, who approves it', async ({ page }) => {
  await signIn(page, CLERK);
  await uploadAndOpen(page, 'price_above_tolerance');
  await expect(page.getByTestId('outcome')).toHaveText('Route for approval');
  const decision = await page.getByTestId('decision').innerText();
  const approver = /Approver: (\S+@\S+)/.exec(decision)?.[1];
  expect(approver).toBeTruthy();
  const url = page.url();

  await page.getByTestId('confirm').click();
  await expect(page.getByTestId('status')).toHaveText('Awaiting approver');
  await expect(page.getByTestId('approve')).toHaveCount(1); // shown, but the server would refuse the clerk
  await signOut(page);

  await signIn(page, approver!);
  await page.goto(url);
  await page.getByTestId('approve').click();
  await expect(page.getByTestId('status')).toHaveText('Approved');
});
