import { useUi } from '@/app/ui'
import { Board } from '@/components/Board'
import { CanaryBanner } from '@/components/CanaryBanner'
import { Evals } from '@/components/Evals'
import { Header } from '@/components/Header'
import { Lessons } from '@/components/Lessons'
import { Modal } from '@/components/Modal'
import { Pipeline } from '@/components/Pipeline'
import { Toast } from '@/components/Toast'

export function App() {
  const { view } = useUi()
  return (
    <>
      <Header />
      <CanaryBanner />
      {view === 'board' && <><Pipeline /><Board /></>}
      {view === 'evals' && <Evals />}
      {view === 'lessons' && <Lessons />}
      <Modal />
      <Toast />
    </>
  )
}
