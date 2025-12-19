import type { Metadata } from "next";
import Link from "next/link";
import "./globals.css";

export const metadata: Metadata = {
  title: "No Engine Brake",
  description: "MVP website for the No Engine Brake community",
};

const navItems = [
  { href: "/", label: "Home" },
  { href: "/aprende", label: "Learn" },
  { href: "/recetas", label: "Recipes" },
  { href: "/comunidad", label: "Community" },
];

function Navigation() {
  return (
    <nav className="bg-black/60 backdrop-blur supports-[backdrop-filter]:bg-black/40">
      <div className="container mx-auto flex items-center justify-between px-6 py-4">
        <Link href="/" className="text-xl font-semibold tracking-tight">
          No Engine Brake
        </Link>
        <ul className="flex gap-4 text-sm font-medium">
          {navItems.map((item) => (
            <li key={item.href}>
              <Link
                href={item.href}
                className="rounded-full px-3 py-2 transition hover:bg-white/10"
              >
                {item.label}
              </Link>
            </li>
          ))}
        </ul>
      </div>
    </nav>
  );
}

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body className="min-h-screen bg-gradient-to-b from-slate-950 via-slate-900 to-slate-950 text-slate-50">
        <Navigation />
        <main className="container mx-auto px-6 py-10 space-y-10">
          {children}
        </main>
        <footer className="border-t border-white/10 bg-black/40 py-6 text-center text-sm text-slate-300">
          MVP Project – No Engine Brake
        </footer>
      </body>
    </html>
  );
}
