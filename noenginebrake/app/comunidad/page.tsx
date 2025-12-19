export default function ComunidadPage() {
  return (
    <section className="space-y-6">
      <h1 className="text-3xl font-semibold">Community coming soon</h1>
      <p className="text-lg text-slate-200">
        We&apos;re fine-tuning the details before opening the community space. In the meantime,
        you can join the waitlist or send us any questions.
      </p>
      <div className="flex flex-col gap-3 sm:flex-row">
        <a
          href="mailto:noenginebrake3@gmail.com?subject=I%20want%20to%20join%20No%20Engine%20Brake"
          className="inline-flex items-center justify-center rounded-lg bg-cyan-400 px-4 py-2 font-semibold text-slate-900 shadow transition hover:bg-cyan-300"
        >
          Join the waitlist
        </a>
        <a
          href="mailto:noenginebrake3@gmail.com?subject=Question%20about%20food%20on%20the%20road"
          className="inline-flex items-center justify-center rounded-lg border border-white/20 px-4 py-2 font-semibold text-cyan-100 transition hover:border-cyan-300 hover:text-white"
        >
          Send a question
        </a>
      </div>
      <div className="rounded-xl border border-white/10 bg-white/5 p-4 shadow">
        <p className="font-medium text-cyan-200">Main channel opening soon</p>
        <p className="text-slate-200">
          There&apos;s no Discord yet, but we&apos;ll let you know as soon as it&apos;s ready so we can
          connect and share resources.
        </p>
      </div>
    </section>
  );
}
