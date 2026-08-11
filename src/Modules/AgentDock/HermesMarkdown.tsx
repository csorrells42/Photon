import { ImageOff } from 'lucide-react'
import ReactMarkdown, { defaultUrlTransform } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import './HermesMarkdown.css'
import { CodeBlock } from './HermesMarkdownCodeBlock'

type Props = { content: string }

function safeUrlTransform(url: string, key: string, node: Readonly<{ tagName: string }>) {
  if (node.tagName === 'img') return ''
  return defaultUrlTransform(url)
}

export function HermesMarkdown({ content }: Props) {
  return (
    <div className="hermes-markdown">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        skipHtml
        urlTransform={safeUrlTransform}
        components={{
          a: ({ children, href, ...properties }) => {
            const external = Boolean(href && /^https?:\/\//i.test(href))
            return <a {...properties} href={href} rel={external ? 'noopener noreferrer' : undefined} target={external ? '_blank' : undefined}>{children}</a>
          },
          img: ({ alt }) => <span className="markdown-image-placeholder" role="note"><ImageOff size={12} /> Remote image not loaded{alt ? `: ${alt}` : ''}</span>,
          pre: ({ children }) => <CodeBlock>{children}</CodeBlock>,
          table: ({ children }) => <div className="markdown-table"><table>{children}</table></div>,
        }}
      >
        {content}
      </ReactMarkdown>
    </div>
  )
}
