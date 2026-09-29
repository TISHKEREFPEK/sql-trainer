import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "SQL-пространство — тренажёр SQL с нуля",
  description: "Короткие уроки SQL, живая песочница SQLite и проекты для практики.",
  icons: {
    icon: "/favicon.svg",
    shortcut: "/favicon.svg",
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="ru">
      <body className="antialiased">{children}</body>
    </html>
  );
}
