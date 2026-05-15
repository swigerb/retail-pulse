import {
  Accordion,
  AccordionItem,
  AccordionHeader,
  AccordionPanel,
  makeStyles,
  tokens,
} from '@fluentui/react-components';

interface CollapsibleSectionProps {
  title: string;
  defaultExpanded?: boolean;
  children: React.ReactNode;
}

const useStyles = makeStyles({
  section: {
    marginBottom: '4px',
  },
  header: {
    fontSize: '13px',
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    color: tokens.colorNeutralForeground2,
  },
});

export function CollapsibleSection({ title, defaultExpanded = false, children }: CollapsibleSectionProps) {
  const styles = useStyles();

  return (
    <div className={styles.section}>
      <Accordion collapsible defaultOpenItems={defaultExpanded ? [title] : []}>
        <AccordionItem value={title}>
          <AccordionHeader className={styles.header}>{title}</AccordionHeader>
          <AccordionPanel>{children}</AccordionPanel>
        </AccordionItem>
      </Accordion>
    </div>
  );
}
