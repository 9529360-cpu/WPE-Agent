import path from 'node:path'

const productionPreviewModule = './lib/runtime-preview.ts'
const developmentPreviewModule = './lib/runtime-preview.development.ts'

/** @type {import('next').NextConfig} */
const nextConfig = {
  output: 'export',
  trailingSlash: true,
  devIndicators: false,
  allowedDevOrigins: ['127.0.0.1'],
  images: {
    unoptimized: true,
  },
  turbopack: {
    resolveAlias: {
      'wpe-runtime-preview': process.env.NODE_ENV === 'development'
        ? developmentPreviewModule
        : productionPreviewModule,
    },
  },
  webpack(config) {
    config.resolve.alias['wpe-runtime-preview'] = path.resolve(
      process.cwd(),
      process.env.NODE_ENV === 'development' ? developmentPreviewModule : productionPreviewModule,
    )
    return config
  },
}

export default nextConfig
