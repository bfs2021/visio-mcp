import { Projection, fixture } from './projection';
import { startBridge } from './bridge';

const p = new Projection(document.getElementById('graph')!);
p.project(fixture());
startBridge(p);
