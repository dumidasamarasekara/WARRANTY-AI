---
namespace: tenant-borealis
documentType: WarrantyPolicy
policyCode: BOR-WP
title: Borealis Care Warranty
version: 1
effectiveFrom: 2024-01-01
effectiveTo: null
regions: [NA, EU]
productCategories: []
classification: Internal
allowedRoles: [adjudication-service, claims-reviewer, auditor]
terms:
  standardCoverageMonths: { NA: 24, EU: 24 }
  componentCoverageMonths: { battery: 12 }
  accidentalDamage: { covered: true, windowMonths: 12, maxIncidents: 1 }
  exclusions: [LIQUID_DAMAGE, UNAUTHORIZED_REPAIR]
clauses:
  BOR-WP-1.1: { type: Coverage }
  BOR-WP-1.2: { type: Coverage }
  BOR-WP-1.3: { type: Definition }
  BOR-WP-2.1: { type: Period }
  BOR-WP-2.2: { type: Period }
  BOR-WP-3.1: { type: Exclusion, exclusionCode: LIQUID_DAMAGE }
  BOR-WP-3.2: { type: Exclusion, exclusionCode: UNAUTHORIZED_REPAIR }
  BOR-WP-4.1: { type: ServiceRule }
  BOR-WP-4.2: { type: ServiceRule }
---
# Borealis Care Warranty (version 1)

## BOR-WP-1.1 Coverage — manufacturing defects

Borealis Devices warrants Borealis tablets, smart-home devices and appliances against defects in
materials and workmanship under normal use during the warranty period. A defect is covered when the
product fails to perform as designed — for example it does not power on, does not heat, loses its
network connection permanently, or the display or a built-in component stops working.

## BOR-WP-1.2 Coverage — accidental damage

Borealis Care includes one accidental-damage incident within the first 12 months from the purchase
date, such as a drop, an impact or a cracked screen. The product is repaired or replaced once; a
second accidental-damage incident, or one after the first 12 months, is not covered.

## BOR-WP-1.3 Definitions

"Purchase date" is the date on the original proof of purchase. "Region" is the region where the
product was purchased; when it cannot be determined, the region of the customer's address applies.
"Battery" means the built-in rechargeable battery and its charging circuitry.

## BOR-WP-2.1 Warranty period

Manufacturing defects are covered for 24 months from the purchase date in all regions. Claims for
defects that first appear after the 24-month period are not covered.

## BOR-WP-2.2 Warranty period — battery

The battery is covered for 12 months from the purchase date in all regions. A battery that no longer
holds a charge, swells or fails to charge after 12 months is not covered.

## BOR-WP-3.1 Exclusions — liquid damage

Damage caused by contact with liquid is not covered, including spills, submersion, condensation and
corrosion of internal components caused by moisture.

## BOR-WP-3.2 Exclusions — unauthorized repair

Damage caused by, and any defect following, a repair, modification or opening of the product by
anyone other than Borealis or a Borealis-authorized service partner is not covered.

## BOR-WP-4.1 Remedy

For a covered defect or covered accidental damage, Borealis will, at its option, repair the product
through an authorized service partner or replace it with an equivalent product. Major appliances
are repaired on site.

## BOR-WP-4.2 Making a claim

A claim must include the product model and serial number, the original proof of purchase, a
description of the problem and photos showing the product, its serial label and the problem.
Borealis may request missing or illegible information before deciding the claim.
